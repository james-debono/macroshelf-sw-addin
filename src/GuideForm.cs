using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace MacroShelf
{
    // The Library > Guide window: a modeless popup explaining how to
    // structure the macro library. Content is embedded HTML rendered by the
    // WebBrowser control so it needs no external files.
    internal class GuideForm : Form
    {
        private static GuideForm _open;

        public static void ShowGuide()
        {
            if (_open != null && !_open.IsDisposed)
            {
                _open.Activate();
                return;
            }
            _open = new GuideForm();
            IWin32Window owner = null;
            try
            {
                IntPtr handle = Process.GetCurrentProcess().MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    owner = new WindowWrapper(handle);
                }
            }
            catch { }
            if (owner != null)
            {
                _open.Show(owner);
            }
            else
            {
                _open.Show();
            }
        }

        private GuideForm()
        {
            Text = "MacroShelf - Library Guide";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(760, 720);
            MinimumSize = new Size(540, 420);
            ShowIcon = false;
            ShowInTaskbar = true;
            MinimizeBox = true;

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 46;

            Button close = new Button();
            close.Text = "Close";
            close.Size = new Size(90, 28);
            close.Click += delegate { Close(); };
            bottom.Controls.Add(close);
            bottom.Layout += delegate
            {
                close.Left = bottom.ClientSize.Width - close.Width - 14;
                close.Top = 9;
            };

            WebBrowser browser = new WebBrowser();
            browser.Dock = DockStyle.Fill;
            browser.AllowNavigation = false;
            browser.AllowWebBrowserDrop = false;
            browser.IsWebBrowserContextMenuEnabled = false;
            browser.WebBrowserShortcutsEnabled = false;
            browser.ScriptErrorsSuppressed = true;
            browser.DocumentText = GuideHtml;

            Controls.Add(browser);
            Controls.Add(bottom);
            browser.BringToFront();
            CancelButton = close;
        }

        private class WindowWrapper : IWin32Window
        {
            private readonly IntPtr _handle;

            public WindowWrapper(IntPtr handle)
            {
                _handle = handle;
            }

            public IntPtr Handle
            {
                get { return _handle; }
            }
        }

        private const string GuideHtml = @"<!DOCTYPE html>
<html>
<head>
<meta http-equiv='X-UA-Compatible' content='IE=edge'>
<style>
  body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 13px; color: #222;
         margin: 18px 24px; background: #ffffff; }
  h1 { font-size: 19px; margin: 0 0 4px 0; }
  h2 { font-size: 14px; margin: 20px 0 6px 0; border-bottom: 1px solid #d8d8d8;
       padding-bottom: 3px; color: #1a3e66; }
  p, li { line-height: 1.5; }
  ul { margin: 6px 0 6px 22px; padding: 0; }
  pre { background: #f4f6f8; border: 1px solid #dde2e8; padding: 10px 14px;
        font-family: Consolas, 'Courier New', monospace; font-size: 12px;
        line-height: 1.5; }
  .muted { color: #666; }
  .tip { background: #eef6ee; border: 1px solid #cfe3cf; padding: 8px 12px;
         margin-top: 6px; }
  b.k { color: #1a3e66; }
</style>
</head>
<body>

<h1>MacroShelf library guide</h1>
<p class='muted'>How to organise your macro library folder so it becomes a tidy toolbar.</p>

<h2>The rule</h2>
<p><b class='k'>A folder is one thing.</b> It holds its macro, its
<b class='k'>icon</b> and its <b class='k'>description</b> &mdash; and the
folder's name is what you see on the toolbar. A drop-down is the same idea one
level up: a folder holding other folders instead of a macro.</p>

<h2>Folder structure</h2>
<pre>
My Macro Library\               &lt;-- the folder you pick in Library &gt; Setup
|
+-- Save As DXF\                &lt;-- a macro folder  =  a normal button
|      SaveAsDxf v2.1.swp             (file name doesn't matter)
|      icon.png                       (optional)
|      description.md                 (optional)
|
+-- Sheet Metal Tools\          &lt;-- folders inside  =  a drop-down button
       icon.png                       (optional - the main button's icon)
       description.md                 (optional - the main button's text)
       |
       +-- Flatten All\         &lt;-- one entry in the drop-down
       |      flatten_0.3.swp
       |      icon.png                (optional)
       |      description.md          (optional)
       |
       +-- Export Flat\         &lt;-- another entry
              export.swp
</pre>

<h2>How folders become buttons</h2>
<ul>
  <li><b class='k'>Folder name = the name you see.</b> Both for buttons and for
      entries in a drop-down. Macro file names are never shown, so version
      numbers in file names are fine &mdash; and replacing a macro with a newer
      file keeps all your settings.</li>
  <li><b class='k'>A folder with one macro file</b> is a button that runs it.</li>
  <li><b class='k'>A folder with macro folders inside</b> is a drop-down, one
      entry per folder.</li>
  <li>Only these two levels are looked at. Anything deeper is ignored.</li>
</ul>

<h2>If a macro doesn't appear</h2>
<p>MacroShelf only shows folders that follow the rule above. Anything else is
listed under <b class='k'>&quot;Not shown&quot;</b> at the bottom of the
Library Manager, with the reason. The usual causes:</p>
<ul>
  <li><b class='k'>Several macro files loose in one folder</b> &mdash; give each
      macro its own folder.</li>
  <li><b class='k'>A macro file and macro folders in the same folder</b> &mdash;
      use one or the other, not both.</li>
  <li><b class='k'>Macro files sitting in the library root</b> &mdash; every
      macro needs a folder.</li>
</ul>

<h2>Icons</h2>
<ul>
  <li>Put an image in a folder and it becomes that folder's icon &mdash; for a
      button, or for one entry in a drop-down. Name it
      <b class='k'>icon.png</b> (or icon.bmp) if there is more than one image;
      otherwise any single image is used.</li>
  <li>A drop-down entry without its own image uses the main button's icon.</li>
  <li><b class='k'>Square, 128 x 128 pixels or larger</b> looks sharp at every
      display scaling. Smaller images get upscaled and can look soft.</li>
  <li>For BMP files, the colour of the <b class='k'>top-left corner pixel</b>
      becomes transparent. PNG files can use real transparency instead.</li>
  <li>No image in the folder? MacroShelf generates a coloured tile with the
      folder's first letter.</li>
  <li><b class='k'>Design tip:</b> SolidWorks shows icons at roughly 20-40 px,
      so a 128 px master gets scaled well down. Use chunky strokes and preview
      your artwork small before exporting &mdash; fine 1-2 px details go soft.
      Export PNG with transparency straight from your design tool rather than
      converting to BMP.</li>
</ul>

<h2>File types</h2>
<ul>
  <li>SolidWorks macros: <b class='k'>.swp</b> (and legacy <b class='k'>.swb</b>).</li>
  <li>A macro should have a normal entry point (<b>Sub main</b>) - if it runs
      from Tools &gt; Macro &gt; Run, it will run from MacroShelf.</li>
</ul>

<h2>Descriptions (hover text)</h2>
<ul>
  <li>A folder's description file must be called
      <b class='k'>description.md</b> (or description.txt) &mdash; that exact
      name, so a readme or licence file is never picked up by mistake.</li>
  <li>On a <b class='k'>toolbar button</b>, the text appears in the tooltip when
      you hover it.</li>
  <li>On an <b class='k'>entry inside a drop-down</b>, SolidWorks doesn't show
      hover boxes, so the text appears in the <b class='k'>status bar at the
      bottom of the SolidWorks window</b> while you hover the entry.</li>
  <li>Without a description file the hover text just says
      &quot;Run [name]&quot;.</li>
</ul>
<p><b>Writing a good description:</b> one or two plain sentences. Start with a
verb, say what the macro acts on and what comes out, and mention anything it
needs first. Long files are trimmed, so lead with the important part.</p>
<div class='tip'>
  Good: <i>&quot;Exports the active drawing as a PDF to your Desktop. Requires
  a drawing to be open.&quot;</i><br>
  Not helpful: <i>&quot;PDF macro v2 (updated by Dave)&quot;</i> &mdash; says
  nothing about what it does or needs.
</div>

<h2>Version numbers</h2>
<p>The Library Manager shows a <b class='k'>Version</b> column beside each macro.
It is read from inside the macro file every time you open the window, so it
always matches the macro that actually runs &mdash; it cannot drift out of step
the way a note kept beside the macro could.</p>
<p>To give your own macro a version, put a comment line of its own near the top
of its code:</p>
<pre>
'   Version   1.0.0
</pre>
<ul>
  <li><b class='k'>Two to four numbers separated by dots</b> &mdash; 1.0.0 is the
      usual form, 1.0 is fine if that is how your macro is numbered, and a fourth
      part like 1.0.0.5 is handy for telling apart copies you hand round while
      testing. A bare 1 is not enough.</li>
  <li>It must be a <b class='k'>comment line</b>, and the version must be the
      last thing on it. <i>' Version 1.0.0 (beta)</i> is not read: once anything
      follows the number, the line is no longer simply a version.</li>
  <li>Capitalisation, indenting and spacing make no difference, and a colon is
      accepted &mdash; <i>' version: 1.0.0</i> works just as well.</li>
</ul>
<div class='tip'>
  The version comes from the <b class='k'>saved macro file</b>. Edit the macro in
  the SolidWorks VBA editor, save it there, then reopen the Library Manager to
  see the change.
</div>
<p class='muted'>A blank simply means the macro carries no such line. That is
normal for macros written by other people, and nothing is wrong.</p>

<h2>Naming tips</h2>
<ul>
  <li>Button text wraps onto multiple lines <b class='k'>at spaces</b>, so give
      folders names with spaces: <i>&quot;Export Flat Pattern&quot;</i> shows as
      two tidy lines, <i>&quot;ExportFlatPattern&quot;</i> stays on one long
      line.</li>
  <li>Buttons are sorted alphabetically by folder name unless you rearrange
      them yourself.</li>
</ul>

<h2>The Library Manager</h2>
<p><b class='k'>Library &gt; Setup</b> opens it. Everything here is personal to
you &mdash; it never changes the library folders themselves.</p>
<ul>
  <li><b class='k'>Add up to 10 libraries</b> (e.g. a shared library on a network
      drive plus a personal one); they all merge into the one toolbar. Untick a library to
      hide everything in it for now &mdash; its buttons vanish from the list and
      the toolbar, and reappear untouched when you tick it back on.</li>
  <li><b class='k'>Untick a button</b> to keep it off your toolbar. Expand a
      drop-down with the arrow to untick <b class='k'>individual macros</b>
      inside it. Turn off all but one and the button becomes a normal
      one-click button; turn them all off and the button disappears.</li>
  <li><b class='k'>Drag buttons</b> to set your own toolbar order. Until you
      first drag, the toolbar sorts alphabetically and new macros slot in
      alphabetically. After your first drag the arrangement is locked as-is and
      newly added macros append at the end. <b class='k'>Sort A-Z</b> returns to
      automatic alphabetical ordering.</li>
</ul>

<h2>Checking for updates</h2>
<p><b class='k'>Library &gt; Check for updates</b> asks GitHub whether a newer
MacroShelf has been released and tells you what it finds. If there is one, it
offers to open the releases page in your browser &mdash; you download and install
the MSI yourself, the same way you installed this one.</p>
<ul>
  <li><b class='k'>It only checks when you click it.</b> Nothing happens when
      SolidWorks starts, there is no timer, and nothing about you or your macros
      is sent &mdash; it is the same request your browser would make opening the
      releases page.</li>
  <li>Nothing is downloaded and nothing is installed automatically.</li>
  <li>After a check finds one, <b class='k'>Update available</b> sits in this
      same menu for a few minutes so you can get back to it.</li>
  <li>If your machine cannot reach GitHub, it says so and points you at the
      releases page. Nothing else is affected.</li>
</ul>

<h2>Updating the toolbar</h2>
<div class='tip'>
  After adding, removing or renaming anything in a library folder, click
  <b class='k'>Library &gt; Scan</b> to refresh the toolbar (it scans all
  libraries; the Library Manager also has a per-library Scan button). The
  toolbar also refreshes itself when SolidWorks starts.
</div>

</body>
</html>";
    }
}
