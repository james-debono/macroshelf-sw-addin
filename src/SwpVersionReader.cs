using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;
using StreamStat = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace MacroShelf
{
    // Reads a macro's own version number out of its .swp file.
    //
    // The version has to come from inside the macro. A sidecar file, or a
    // version written into description.md, can be made to disagree with the
    // macro sitting beside it simply by swapping the .swp - so the only
    // trustworthy answer is the one the running code carries.
    //
    // A .swp is an OLE compound file and must be read as one. Its free sectors
    // retain earlier copies of the source, so scanning the raw bytes for the
    // version string finds old and new side by side with no way to tell which
    // is live - that produced one false result already. Reading through
    // StgOpenStorage and an IStream respects each stream's declared length, so
    // the slack sectors are never in reach.
    //
    // Inside, the VBA source lives in a storage named "VBA" (in practice at
    // "apc\The VBA Project\_VBA_Project\VBA", but it is searched for rather
    // than assumed, so a future SolidWorks that moves it still works). Each
    // module stream holds an opaque p-code cache followed by the source
    // compressed with the MS-OVBA scheme; the "dir" stream, itself compressed,
    // records where each module's source begins.
    //
    // Nothing here caches. Versions are read when the Library Manager opens -
    // a deliberate user action where the cost is invisible - so every reading
    // is fresh. A cache keyed on path and write time would mostly work, and a
    // wrong version number is worse than no version number.
    internal static class SwpVersionReader
    {
        // A comment line, on its own, naming a version:
        //     '   Version   0.11.2
        //
        // Two to four parts. Older macros are commonly versioned "1.0", and
        // showing that beats showing nothing; a fourth part identifies a build
        // handed over for testing, which is what MacroShelf's own fourth field
        // does. Five or more is not a version number anybody means.
        //
        // Deliberately forgiving about the things people get wrong without
        // meaning anything by it - capitalisation, indentation, a colon after
        // the word - because this is a documented format that authors of other
        // macros are invited to adopt.
        //
        // Strict about two things, though. A digit must follow the word, and
        // nothing may follow the number. Both matter: a real macro in the
        // archive opens a comment with "' version of swconst, so that one is
        // read...", and a line like "' Version 1.0.0 (beta)" is not simply a
        // version statement. Guessing at either would put a number on screen
        // that means something else.
        private static readonly Regex VersionLine = new Regex(
            @"^\s*'\s*Version(?:\s*:\s*|\s+)(\d+\.\d+(?:\.\d+){0,2})\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Guard rails. A macro is tens of kilobytes; these only stop a corrupt
        // or hostile file from turning into an allocation.
        private const int MaxStreamBytes = 64 * 1024 * 1024;
        private const int MaxChunkOutput = 4096;   // MS-OVBA: a chunk decompresses to at most this
        private const int MaxLineLength = 512;
        private const int MaxElements = 512;
        private const int MaxSearchDepth = 8;
        private const int MaxScanCandidates = 64;

        // Returns the version a .swp declares, or null when it declares none,
        // cannot be read, or is not a macro at all. Never throws: a macro
        // without a version line simply shows nothing, which is the right
        // outcome for somebody else's macro.
        public static string Read(string macroPath)
        {
            if (string.IsNullOrEmpty(macroPath))
            {
                return null;
            }
            try
            {
                if (!File.Exists(macroPath))
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }

            IStorage root = null;
            try
            {
                root = OpenCompoundFile(macroPath);
                if (root == null)
                {
                    return null;
                }
                List<string> path = FindVbaStoragePath(root, 0);
                if (path == null)
                {
                    return null;
                }
                return ReadThroughPath(root, path);
            }
            catch
            {
                return null;
            }
            finally
            {
                Release(root);
            }
        }

        // Fills in Version on every macro of every button. Used by the Library
        // Manager, which is the only place that reads versions.
        public static void FillVersions(IEnumerable<MacroButton> buttons)
        {
            if (buttons == null)
            {
                return;
            }
            foreach (MacroButton button in buttons)
            {
                if (button == null || button.Macros == null)
                {
                    continue;
                }
                foreach (MacroCommand macro in button.Macros)
                {
                    if (macro != null)
                    {
                        macro.Version = Read(macro.MacroPath);
                    }
                }
            }
        }

        // ----- the compound file -----

        private static IStorage OpenCompoundFile(string path)
        {
            IStorage storage;
            int hr = StgOpenStorage(path, null, STGM_READ | STGM_SHARE_DENY_WRITE,
                IntPtr.Zero, 0, out storage);
            if (hr == 0 && storage != null)
            {
                return storage;
            }
            // Deny-write fails while something else holds the file open for
            // writing - the VBA editor, most likely, with this very macro up on
            // screen. A transacted read shares with it.
            hr = StgOpenStorage(path, null, STGM_READ | STGM_SHARE_DENY_NONE | STGM_TRANSACTED,
                IntPtr.Zero, 0, out storage);
            return hr == 0 ? storage : null;
        }

        // Finds the "VBA" storage by name, returning the chain of storage names
        // that leads to it. A path rather than the storage itself, so that every
        // storage opened along the way has an obvious owner to release it.
        private static List<string> FindVbaStoragePath(IStorage storage, int depth)
        {
            if (depth >= MaxSearchDepth)
            {
                return null;
            }
            foreach (string name in ListElements(storage, STGTY_STORAGE))
            {
                IStorage child = null;
                try
                {
                    child = OpenChildStorage(storage, name);
                    if (child == null)
                    {
                        continue;
                    }
                    if (string.Equals(name, "VBA", StringComparison.OrdinalIgnoreCase)
                        && ListElements(child, STGTY_STREAM).Contains("dir"))
                    {
                        List<string> found = new List<string>();
                        found.Add(name);
                        return found;
                    }
                    List<string> deeper = FindVbaStoragePath(child, depth + 1);
                    if (deeper != null)
                    {
                        deeper.Insert(0, name);
                        return deeper;
                    }
                }
                catch
                {
                    // An unreadable branch is not a reason to give up on the rest.
                }
                finally
                {
                    Release(child);
                }
            }
            return null;
        }

        private static string ReadThroughPath(IStorage root, List<string> path)
        {
            List<IStorage> opened = new List<IStorage>();
            try
            {
                IStorage current = root;
                foreach (string name in path)
                {
                    IStorage next = OpenChildStorage(current, name);
                    if (next == null)
                    {
                        return null;
                    }
                    opened.Add(next);
                    current = next;
                }
                return ReadFromVbaStorage(current);
            }
            finally
            {
                for (int i = opened.Count - 1; i >= 0; i--)
                {
                    Release(opened[i]);
                }
            }
        }

        private static string ReadFromVbaStorage(IStorage vba)
        {
            List<ModuleEntry> modules = new List<ModuleEntry>();
            byte[] dir = ReadStream(vba, "dir");
            if (dir != null)
            {
                byte[] table = Decompress(dir, 0);
                if (table != null)
                {
                    modules = ParseDir(table);
                }
            }

            foreach (ModuleEntry module in modules)
            {
                byte[] stream = ReadStream(vba, module.StreamName);
                if (stream == null)
                {
                    continue;
                }
                // The recorded offset is the fast path: one decompression per
                // module, starting exactly at the source.
                string version = DecompressAndFindVersion(stream, module.TextOffset);
                if (version != null)
                {
                    return version;
                }
                // The offset can be wrong - dir layout has version-specific
                // quirks - so fall back to finding the compressed source inside
                // the stream. Bounded, and still confined to the stream.
                if (!LooksLikeContainer(stream, module.TextOffset))
                {
                    version = ScanStreamForVersion(stream);
                    if (version != null)
                    {
                        return version;
                    }
                }
            }

            if (modules.Count > 0)
            {
                return null; // dir was readable and no module declares a version
            }

            // dir could not be parsed at all: try every stream in the storage.
            foreach (string name in ListElements(vba, STGTY_STREAM))
            {
                if (string.Equals(name, "dir", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "_VBA_PROJECT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                byte[] stream = ReadStream(vba, name);
                if (stream == null)
                {
                    continue;
                }
                string version = ScanStreamForVersion(stream);
                if (version != null)
                {
                    return version;
                }
            }
            return null;
        }

        // ----- the dir stream -----

        // internal, not private, so the offline tests can exercise the dir
        // walk and the decompressor directly rather than only through a .swp.
        internal class ModuleEntry
        {
            public string StreamName;
            public int TextOffset;
        }

        private const int RecordProjectVersion = 0x0009;
        private const int RecordModuleStreamName = 0x001A;
        private const int RecordModuleStreamNameUnicode = 0x0032;
        private const int RecordModuleOffset = 0x0031;

        // PROJECTVERSION is the one record whose size field lies: it reads 4,
        // but the record carries 6 bytes (VersionMajor 4 + VersionMinor 2).
        // Trusting the field leaves the walk two bytes out of step and every
        // record after it is garbage - which is exactly how this first failed.
        private const int ProjectVersionDataBytes = 6;

        // Walks the decompressed dir stream as a flat list of
        // id / size / data records, pairing each module's stream name with the
        // offset at which its source starts. Returns what it has if the walk
        // desynchronises, so the caller can fall back to scanning.
        internal static List<ModuleEntry> ParseDir(byte[] dir)
        {
            List<ModuleEntry> modules = new List<ModuleEntry>();
            string pending = null;
            int pos = 0;
            while (pos + 6 <= dir.Length)
            {
                int id = dir[pos] | (dir[pos + 1] << 8);
                long size = (uint)(dir[pos + 2] | (dir[pos + 3] << 8)
                    | (dir[pos + 4] << 16) | (dir[pos + 5] << 24));
                if (id == RecordProjectVersion)
                {
                    size = ProjectVersionDataBytes;
                }
                int body = pos + 6;
                if (size < 0 || size > dir.Length - body)
                {
                    break;
                }
                int length = (int)size;
                if (id == RecordModuleStreamNameUnicode && length > 0)
                {
                    pending = Encoding.Unicode.GetString(dir, body, length);
                }
                else if (id == RecordModuleStreamName && length > 0)
                {
                    // Superseded by the Unicode record that follows it, when
                    // one is present. The project code page would be exact;
                    // module names are ASCII in practice.
                    pending = Encoding.Default.GetString(dir, body, length);
                }
                else if (id == RecordModuleOffset && length == 4 && pending != null)
                {
                    ModuleEntry entry = new ModuleEntry();
                    entry.StreamName = pending;
                    entry.TextOffset = dir[body] | (dir[body + 1] << 8)
                        | (dir[body + 2] << 16) | (dir[body + 3] << 24);
                    if (entry.TextOffset >= 0)
                    {
                        modules.Add(entry);
                    }
                    pending = null;
                }
                pos = body + length;
            }
            return modules;
        }

        // ----- MS-OVBA decompression -----
        //
        // A CompressedContainer is a 0x01 signature byte followed by chunks.
        // Each chunk has a two-byte header: bits 0-11 hold its size minus 3,
        // bits 12-14 a 0b011 signature, bit 15 a flag saying whether the chunk
        // is compressed at all. Inside a compressed chunk, every eight tokens
        // are introduced by a flag byte, one bit each, least significant first:
        // 0 means a literal byte, 1 means a two-byte copy token. A copy token's
        // split between offset bits and length bits widens as the chunk's own
        // output grows, and it can only ever refer back within the current
        // chunk - which is why the search below can stop at a chunk boundary
        // the moment it has the line it wants.

        private const byte CompressedSignature = 0x01;
        private const int ChunkSignature = 0x03;

        private static bool LooksLikeContainer(byte[] data, int start)
        {
            if (data == null || start < 0 || start + 2 >= data.Length)
            {
                return false;
            }
            if (data[start] != CompressedSignature)
            {
                return false;
            }
            int header = data[start + 1] | (data[start + 2] << 8);
            return ((header >> 12) & 0x07) == ChunkSignature;
        }

        // Decompresses a whole container. Only used for the dir stream, which
        // is small; module streams go through DecompressAndFindVersion so they
        // can stop as soon as the version line turns up.
        internal static byte[] Decompress(byte[] data, int start)
        {
            if (!LooksLikeContainer(data, start))
            {
                return null;
            }
            MemoryStream output = new MemoryStream();
            byte[] window = new byte[MaxChunkOutput];
            int pos = start + 1;
            while (pos + 1 < data.Length)
            {
                int produced = ReadChunk(data, ref pos, window);
                if (produced < 0)
                {
                    return output.Length > 0 ? output.ToArray() : null;
                }
                output.Write(window, 0, produced);
            }
            return output.ToArray();
        }

        // Decompresses a container one chunk at a time, splitting the output
        // into lines and stopping at the first version line. The header sits in
        // the first few hundred bytes of a macro, so in practice this reads one
        // 4 KB chunk of a stream that may be a hundred kilobytes long.
        private static string DecompressAndFindVersion(byte[] data, int start)
        {
            if (!LooksLikeContainer(data, start))
            {
                return null;
            }
            byte[] window = new byte[MaxChunkOutput];
            StringBuilder line = new StringBuilder(MaxLineLength);
            int pos = start + 1;
            while (pos + 1 < data.Length)
            {
                int produced = ReadChunk(data, ref pos, window);
                if (produced < 0)
                {
                    break;
                }
                string version = ScanLines(window, produced, line);
                if (version != null)
                {
                    return version;
                }
            }
            return MatchLine(line);
        }

        // Decompresses one chunk into window, advancing pos. Returns the number
        // of bytes produced, or -1 if the data stops making sense.
        private static int ReadChunk(byte[] data, ref int pos, byte[] window)
        {
            if (pos + 1 >= data.Length)
            {
                return -1;
            }
            int header = data[pos] | (data[pos + 1] << 8);
            pos += 2;
            if (((header >> 12) & 0x07) != ChunkSignature)
            {
                return -1;
            }
            int size = (header & 0x0FFF) + 3;
            bool compressed = (header & 0x8000) != 0;
            int end = pos + size - 2;
            if (end > data.Length)
            {
                end = data.Length;
            }

            int produced = 0;
            if (!compressed)
            {
                while (pos < end && produced < window.Length)
                {
                    window[produced++] = data[pos++];
                }
                pos = end;
                return produced;
            }

            while (pos < end)
            {
                int flags = data[pos++];
                for (int bit = 0; bit < 8 && pos < end; bit++)
                {
                    if (((flags >> bit) & 1) == 0)
                    {
                        if (produced >= window.Length)
                        {
                            return -1;
                        }
                        window[produced++] = data[pos++];
                        continue;
                    }
                    if (pos + 1 >= data.Length)
                    {
                        return -1;
                    }
                    int token = data[pos] | (data[pos + 1] << 8);
                    pos += 2;
                    int bits = 4;
                    while ((1 << bits) < produced)
                    {
                        bits++;
                    }
                    if (bits > 12)
                    {
                        bits = 12;
                    }
                    int length = (token & (0xFFFF >> bits)) + 3;
                    int offset = (token >> (16 - bits)) + 1;
                    int source = produced - offset;
                    if (source < 0 || produced + length > window.Length)
                    {
                        return -1;
                    }
                    for (int i = 0; i < length; i++)
                    {
                        window[produced++] = window[source++];
                    }
                }
            }
            return produced;
        }

        // Feeds freshly decompressed bytes through a line splitter, testing
        // each complete line. Working a line at a time avoids ever holding the
        // whole module as a string.
        private static string ScanLines(byte[] buffer, int count, StringBuilder line)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = buffer[i];
                if (b == (byte)'\n')
                {
                    string version = MatchLine(line);
                    line.Length = 0;
                    if (version != null)
                    {
                        return version;
                    }
                }
                else if (line.Length < MaxLineLength)
                {
                    line.Append((char)b);
                }
            }
            return null;
        }

        // The rule itself, on one line of source. internal so the tests can pin
        // down which forms count and which do not without needing a .swp.
        internal static string MatchVersionLine(string line)
        {
            if (line == null)
            {
                return null;
            }
            Match match = VersionLine.Match(line);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string MatchLine(StringBuilder line)
        {
            // Reject anything that is not a comment before building a string or
            // running the regex: most lines of a macro are code, and every one
            // of them comes through here.
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }
            if (i >= line.Length || line[i] != '\'')
            {
                return null;
            }
            return MatchVersionLine(line.ToString());
        }

        // Fallback for when the recorded offset does not point at the source.
        // Safe in a way that scanning the file is not: a stream read stops at
        // the stream's declared length, so no free-sector leftovers are in
        // reach, and comments do not survive into the p-code cache anyway.
        private static string ScanStreamForVersion(byte[] stream)
        {
            int candidates = 0;
            for (int i = 0; i + 2 < stream.Length && candidates < MaxScanCandidates; i++)
            {
                if (!LooksLikeContainer(stream, i))
                {
                    continue;
                }
                candidates++;
                string version = DecompressAndFindVersion(stream, i);
                if (version != null)
                {
                    return version;
                }
            }
            return null;
        }

        // ----- structured storage plumbing -----

        private static IStorage OpenChildStorage(IStorage parent, string name)
        {
            IStorage child;
            int hr = parent.OpenStorage(name, null, STGM_READ | STGM_SHARE_EXCLUSIVE,
                IntPtr.Zero, 0, out child);
            return hr == 0 ? child : null;
        }

        private static List<string> ListElements(IStorage storage, uint wantedType)
        {
            List<string> names = new List<string>();
            IEnumSTATSTG enumerator = null;
            try
            {
                int hr = storage.EnumElements(0, IntPtr.Zero, 0, out enumerator);
                if (hr != 0 || enumerator == null)
                {
                    return names;
                }
                RawStatStg[] one = new RawStatStg[1];
                uint fetched;
                while (names.Count < MaxElements
                    && enumerator.Next(1, one, out fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        if (one[0].type == wantedType && one[0].pwcsName != IntPtr.Zero)
                        {
                            names.Add(Marshal.PtrToStringUni(one[0].pwcsName));
                        }
                    }
                    finally
                    {
                        // The enumerator allocates the name and hands ownership
                        // over. Marshalling it as a string would leak it, which
                        // is why RawStatStg keeps it as a pointer.
                        if (one[0].pwcsName != IntPtr.Zero)
                        {
                            Marshal.FreeCoTaskMem(one[0].pwcsName);
                            one[0].pwcsName = IntPtr.Zero;
                        }
                    }
                }
            }
            catch
            {
                // A storage that will not enumerate simply has nothing to offer.
            }
            finally
            {
                Release(enumerator);
            }
            return names;
        }

        private static byte[] ReadStream(IStorage storage, string name)
        {
            IStream stream = null;
            IntPtr readCount = IntPtr.Zero;
            try
            {
                int hr = storage.OpenStream(name, IntPtr.Zero,
                    STGM_READ | STGM_SHARE_EXCLUSIVE, 0, out stream);
                if (hr != 0 || stream == null)
                {
                    return null;
                }
                StreamStat stat;
                stream.Stat(out stat, STATFLAG_NONAME);
                if (stat.cbSize <= 0 || stat.cbSize > MaxStreamBytes)
                {
                    return null;
                }
                byte[] buffer = new byte[(int)stat.cbSize];
                byte[] chunk = new byte[64 * 1024];
                readCount = Marshal.AllocHGlobal(sizeof(int));
                int filled = 0;
                while (filled < buffer.Length)
                {
                    int want = Math.Min(chunk.Length, buffer.Length - filled);
                    stream.Read(chunk, want, readCount);
                    int got = Marshal.ReadInt32(readCount);
                    if (got <= 0)
                    {
                        break;
                    }
                    if (got > want)
                    {
                        got = want;
                    }
                    Buffer.BlockCopy(chunk, 0, buffer, filled, got);
                    filled += got;
                }
                if (filled == 0)
                {
                    return null;
                }
                if (filled < buffer.Length)
                {
                    byte[] shorter = new byte[filled];
                    Buffer.BlockCopy(buffer, 0, shorter, 0, filled);
                    return shorter;
                }
                return buffer;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (readCount != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(readCount);
                }
                Release(stream);
            }
        }

        // Every COM object here is released explicitly. A storage left alive
        // keeps a handle on the .swp, and a .swp is a file the user expects to
        // be able to save over from the VBA editor at any moment.
        private static void Release(object comObject)
        {
            try
            {
                if (comObject != null && Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch { }
        }

        // ----- COM interop -----

        private const uint STGM_READ = 0x00000000;
        private const uint STGM_SHARE_EXCLUSIVE = 0x00000010;
        private const uint STGM_SHARE_DENY_WRITE = 0x00000020;
        private const uint STGM_SHARE_DENY_NONE = 0x00000040;
        private const uint STGM_TRANSACTED = 0x00010000;
        private const uint STGTY_STORAGE = 1;
        private const uint STGTY_STREAM = 2;
        private const int STATFLAG_NONAME = 1;   // ComTypes.IStream.Stat takes an int

        [DllImport("ole32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StgOpenStorage(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
            IStorage pstgPriority,
            uint grfMode,
            IntPtr snbExclude,
            uint reserved,
            out IStorage ppstgOpen);

        // STATSTG with the name left as a raw pointer, so it can be freed
        // rather than leaked - see ListElements.
        [StructLayout(LayoutKind.Sequential)]
        private struct RawStatStg
        {
            public IntPtr pwcsName;
            public uint type;
            public long cbSize;
            public FILETIME mtime;
            public FILETIME ctime;
            public FILETIME atime;
            public uint grfMode;
            public uint grfLocksSupported;
            public Guid clsid;
            public uint grfStateBits;
            public uint reserved;
        }

        [ComImport]
        [Guid("0000000D-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumSTATSTG
        {
            [PreserveSig]
            int Next(uint celt,
                [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] RawStatStg[] rgelt,
                out uint pceltFetched);
            [PreserveSig]
            int Skip(uint celt);
            [PreserveSig]
            int Reset();
            void Clone(out IEnumSTATSTG ppenum);
        }

        // Only OpenStream, OpenStorage and EnumElements are ever called. The
        // rest are declared to hold their places in the COM vtable, which is
        // positional - removing one would silently call the wrong method.
        [ComImport]
        [Guid("0000000B-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IStorage
        {
            void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
                uint grfMode, uint reserved1, uint reserved2, out IStream ppstm);
            [PreserveSig]
            int OpenStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
                IntPtr reserved1, uint grfMode, uint reserved2, out IStream ppstm);
            void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
                uint grfMode, uint reserved1, uint reserved2, out IStorage ppstg);
            [PreserveSig]
            int OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
                IStorage pstgPriority, uint grfMode, IntPtr snbExclude, uint reserved,
                out IStorage ppstg);
            void CopyTo(uint ciidExclude, IntPtr rgiidExclude, IntPtr snbExclude,
                IStorage pstgDest);
            void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
                IStorage pstgDest, [MarshalAs(UnmanagedType.LPWStr)] string pwcsNewName,
                uint grfFlags);
            void Commit(uint grfCommitFlags);
            void Revert();
            [PreserveSig]
            int EnumElements(uint reserved1, IntPtr reserved2, uint reserved3,
                out IEnumSTATSTG ppenum);
            void DestroyElement([MarshalAs(UnmanagedType.LPWStr)] string pwcsName);
            void RenameElement([MarshalAs(UnmanagedType.LPWStr)] string pwcsOldName,
                [MarshalAs(UnmanagedType.LPWStr)] string pwcsNewName);
            void SetElementTimes([MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
                IntPtr pctime, IntPtr patime, IntPtr pmtime);
            void SetClass(ref Guid clsid);
            void SetStateBits(uint grfStateBits, uint grfMask);
            void Stat(out RawStatStg pstatstg, uint grfStatFlag);
        }
    }
}
