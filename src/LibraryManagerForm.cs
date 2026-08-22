using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacroDeck
{
    // The Library > Setup window: manage up to 10 macro libraries, switch
    // libraries, buttons and individual macros on or off, and drag buttons to
    // set the toolbar order. Works on an in-memory copy of the settings;
    // nothing is persisted until OK.
    internal class LibraryManagerForm : Form
    {
        private const int LibraryEnabledColumn = 0;
        private const int LibraryScanColumn = 4;

        // The version column down the right-hand edge of the tree. A TreeView
        // has no columns of its own, so the label is drawn by hand and the
        // version right-aligned in the space reserved here.
        //
        // Wide enough for every version shape the reader accepts, up to four
        // parts: "10.20.30.40" measures 64px at the default font. Anything
        // longer is ellipsised rather than clipped - right-aligned text that
        // simply runs out of room loses its leading digits, and half a version
        // number reads as a different version number.
        private const int VersionColumnWidth = 84;
        private const int VersionColumnGap = 12;

        private readonly SettingsData _settings;
        private List<MacroButton> _buttons = new List<MacroButton>();
        private List<SkippedFolder> _skipped = new List<SkippedFolder>();

        private DataGridView _grid;
        private TreeView _tree;
        private Label _versionHeader;
        private Label _skippedLabel;
        private ListBox _skippedList;
        private TableLayoutPanel _layout;
        private bool _suppressCheckEvents;
        private TreeNode _dragNode;

        // Shows the manager modally over SolidWorks. Returns true when the
        // user pressed OK (settings saved; caller should rebuild the toolbar).
        public static bool ShowManager()
        {
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

            using (LibraryManagerForm form = new LibraryManagerForm())
            {
                DialogResult result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
                if (result == DialogResult.OK)
                {
                    Settings.Save(form._settings);
                    return true;
                }
                return false;
            }
        }

        private LibraryManagerForm()
        {
            _settings = Settings.Load();
            BuildLayout();
            RefreshAll();
        }

        // ----- layout -----

        private void BuildLayout()
        {
            Text = "MacroDeck - Library Manager (" + MacroDeckAddin.VersionString() + ")";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(720, 700);
            MinimumSize = new Size(620, 560);
            ShowIcon = false;
            ShowInTaskbar = true;
            MinimizeBox = false;

            _layout = new TableLayoutPanel();
            _layout.Dock = DockStyle.Fill;
            _layout.Padding = new Padding(12);
            _layout.ColumnCount = 1;
            _layout.RowCount = 9;
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 0 libraries label
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130)); // 1 grid
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 2 library buttons
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));  // 3 tree label + version header
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 4 tree
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 5 tree buttons
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 6 skipped label
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));  // 7 skipped list
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 8 ok/cancel

            Label libLabel = new Label();
            libLabel.Text = "Macro libraries (up to " + Settings.MaxLibraries
                + "). Untick a library to hide all of its buttons:";
            libLabel.AutoSize = true;
            libLabel.Margin = new Padding(0, 0, 0, 4);
            _layout.Controls.Add(libLabel, 0, 0);

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

            DataGridViewCheckBoxColumn onCol = new DataGridViewCheckBoxColumn();
            onCol.HeaderText = "On";
            onCol.Width = 34;
            onCol.ReadOnly = false;
            DataGridViewTextBoxColumn pathCol = new DataGridViewTextBoxColumn();
            pathCol.HeaderText = "Library folder";
            pathCol.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            pathCol.ReadOnly = true;
            DataGridViewTextBoxColumn countCol = new DataGridViewTextBoxColumn();
            countCol.HeaderText = "Buttons";
            countCol.Width = 60;
            countCol.ReadOnly = true;
            DataGridViewTextBoxColumn statusCol = new DataGridViewTextBoxColumn();
            statusCol.HeaderText = "Status";
            statusCol.Width = 70;
            statusCol.ReadOnly = true;
            DataGridViewButtonColumn scanCol = new DataGridViewButtonColumn();
            scanCol.HeaderText = "";
            scanCol.Text = "Scan";
            scanCol.UseColumnTextForButtonValue = true;
            scanCol.Width = 60;
            _grid.Columns.AddRange(new DataGridViewColumn[] { onCol, pathCol, countCol, statusCol, scanCol });
            _grid.CellClick += OnGridCellClick;
            _grid.CurrentCellDirtyStateChanged += OnGridDirtyStateChanged;
            _grid.CellValueChanged += OnGridCellValueChanged;
            _layout.Controls.Add(_grid, 0, 1);

            FlowLayoutPanel libButtons = new FlowLayoutPanel();
            libButtons.AutoSize = true;
            libButtons.Margin = new Padding(0, 6, 0, 10);
            libButtons.Controls.Add(MakeButton("Add Library...", OnAddLibrary));
            libButtons.Controls.Add(MakeButton("Remove", OnRemoveLibrary));
            _layout.Controls.Add(libButtons, 0, 2);

            // The tree's header row: the instructions on the left, and the
            // version column's heading sitting over the numbers on the right.
            Panel treeHeader = new Panel();
            treeHeader.Dock = DockStyle.Fill;
            treeHeader.Margin = new Padding(0, 0, 0, 4);

            _versionHeader = new Label();
            _versionHeader.Text = "Version";
            _versionHeader.Dock = DockStyle.Right;
            _versionHeader.Width = VersionColumnWidth;
            _versionHeader.TextAlign = ContentAlignment.BottomRight;
            _versionHeader.ForeColor = SystemColors.GrayText;

            Label treeLabel = new Label();
            treeLabel.Text = "Toolbar buttons - untick to hide, expand to switch individual macros off, "
                + "drag to change the order:";
            treeLabel.Dock = DockStyle.Fill;
            treeLabel.AutoEllipsis = true; // rather than run under the Version heading
            treeLabel.TextAlign = ContentAlignment.BottomLeft;

            // Docking is resolved last-added-first, so the Fill goes in first
            // and the column heading claims its edge from what remains.
            treeHeader.Controls.Add(treeLabel);
            treeHeader.Controls.Add(_versionHeader);
            _layout.Controls.Add(treeHeader, 0, 3);

            _tree = new TreeView();
            _tree.Dock = DockStyle.Fill;
            _tree.CheckBoxes = true;
            _tree.HideSelection = false;
            _tree.ShowRootLines = true;  // needed for the expand arrows on root nodes
            _tree.ShowPlusMinus = true;
            _tree.ShowLines = false;
            _tree.FullRowSelect = false;
            _tree.AllowDrop = true;
            _tree.ItemHeight = 20;
            // Only the label is drawn by hand; the checkboxes, the expand
            // arrows and the drag insertion mark stay native.
            _tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            _tree.DrawNode += OnTreeDrawNode;
            // Fires when the tree is resized and, importantly, when the list
            // grows long enough for a scrollbar to appear and take 17 pixels
            // off the column.
            _tree.ClientSizeChanged += OnTreeClientSizeChanged;
            _tree.AfterCheck += OnTreeAfterCheck;
            _tree.ItemDrag += OnTreeItemDrag;
            _tree.DragOver += OnTreeDragOver;
            _tree.DragDrop += OnTreeDragDrop;
            _tree.DragLeave += OnTreeDragLeave;
            _layout.Controls.Add(_tree, 0, 4);

            FlowLayoutPanel treeButtons = new FlowLayoutPanel();
            treeButtons.AutoSize = true;
            treeButtons.Margin = new Padding(0, 6, 0, 10);
            treeButtons.Controls.Add(MakeButton("Sort A-Z", OnSortAlphabetical));
            treeButtons.Controls.Add(MakeButton("Expand All", delegate { _tree.ExpandAll(); }));
            treeButtons.Controls.Add(MakeButton("Collapse All", delegate { _tree.CollapseAll(); }));
            _layout.Controls.Add(treeButtons, 0, 5);

            _skippedLabel = new Label();
            _skippedLabel.AutoSize = true;
            _skippedLabel.ForeColor = Color.Firebrick;
            _skippedLabel.Margin = new Padding(0, 0, 0, 4);
            _layout.Controls.Add(_skippedLabel, 0, 6);

            _skippedList = new ListBox();
            _skippedList.Dock = DockStyle.Fill;
            _skippedList.IntegralHeight = false;
            _skippedList.HorizontalScrollbar = true;
            _layout.Controls.Add(_skippedList, 0, 7);

            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.FlowDirection = FlowDirection.RightToLeft;
            bottom.Dock = DockStyle.Fill;
            bottom.AutoSize = true;
            bottom.Margin = new Padding(0, 8, 0, 0);
            Button cancel = MakeButton("Cancel", null);
            cancel.DialogResult = DialogResult.Cancel;
            Button ok = MakeButton("OK", null);
            ok.DialogResult = DialogResult.OK;
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);
            _layout.Controls.Add(bottom, 0, 8);

            Controls.Add(_layout);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static Button MakeButton(string text, EventHandler onClick)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.Padding = new Padding(6, 2, 6, 2);
            if (onClick != null)
            {
                button.Click += onClick;
            }
            return button;
        }

        // ----- data -> UI -----

        private void RefreshAll()
        {
            ScanResult scan = LibraryScanner.ScanAll(_settings.Libraries);
            _buttons = LibraryScanner.ApplyPrefs(scan.Buttons, _settings);
            // Versions are read here and nowhere else: opening this window, or
            // adding a library to it, is a deliberate act where a few
            // milliseconds go unnoticed. Nothing is cached, so what is on
            // screen is what is in the files right now.
            SwpVersionReader.FillVersions(_buttons);
            _skipped = scan.Skipped;
            RefreshGrid();
            RefreshTree();
            RefreshSkipped();
        }

        private void RefreshGrid()
        {
            _suppressCheckEvents = true;
            try
            {
                _grid.Rows.Clear();
                foreach (string library in _settings.Libraries)
                {
                    int count = _buttons.Count(b =>
                        string.Equals(b.LibraryPath, library, StringComparison.OrdinalIgnoreCase));
                    bool exists = Directory.Exists(library);
                    int row = _grid.Rows.Add(
                        Settings.IsLibraryEnabled(_settings, library),
                        library, count, exists ? "OK" : "missing", "Scan");
                    if (!exists)
                    {
                        _grid.Rows[row].Cells[3].Style.ForeColor = Color.Firebrick;
                    }
                }
            }
            finally
            {
                _suppressCheckEvents = false;
            }
        }

        private void RefreshTree()
        {
            _suppressCheckEvents = true;
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                foreach (MacroButton button in VisibleButtons())
                {
                    TreeNode node = new TreeNode(ButtonLabel(button));
                    node.Tag = button;
                    node.Checked = button.Enabled;
                    node.ToolTipText = button.FolderPath;
                    if (button.IsMulti)
                    {
                        foreach (MacroCommand macro in button.Macros)
                        {
                            TreeNode child = new TreeNode(macro.DisplayName);
                            child.Tag = macro;
                            child.Checked = macro.Enabled;
                            child.ToolTipText = macro.FolderPath;
                            node.Nodes.Add(child);
                        }
                    }
                    _tree.Nodes.Add(node);
                }
                _tree.CollapseAll(); // children stay tucked away until needed
            }
            finally
            {
                _tree.EndUpdate();
                _suppressCheckEvents = false;
            }
            // A heading over a column with nothing in it just looks broken, so
            // a library of macros that declare no version shows neither.
            _versionHeader.Visible = VisibleButtons().Any(
                b => b.Macros.Any(m => !string.IsNullOrEmpty(m.Version)));
            AlignVersionHeader();
        }

        // ----- the version column -----

        private void OnTreeClientSizeChanged(object sender, EventArgs e)
        {
            AlignVersionHeader();
        }

        // Lines the "Version" heading up with the numbers underneath it. The
        // tree's border - and its scrollbar, once the list is long enough to
        // need one - sit between the column and the edge of the window, so the
        // heading cannot simply hug that edge or it drifts out of step.
        private void AlignVersionHeader()
        {
            if (_versionHeader == null || _tree == null || !_tree.IsHandleCreated)
            {
                return;
            }
            Control header = _versionHeader.Parent;
            if (header == null)
            {
                return;
            }
            try
            {
                Point columnEdge = _tree.PointToScreen(
                    new Point(_tree.ClientSize.Width - VersionColumnGap, 0));
                int inset = header.ClientSize.Width - header.PointToClient(columnEdge).X;
                if (inset < 0)
                {
                    inset = 0;
                }
                if (_versionHeader.Padding.Right != inset)
                {
                    _versionHeader.Width = VersionColumnWidth + inset;
                    _versionHeader.Padding = new Padding(0, 0, inset, 0);
                }
            }
            catch
            {
                // Nothing here is worth failing the window over.
            }
        }

        // A TreeView draws one label per row and has no notion of a column, so
        // the row is painted here: the name where it always was, and the
        // version right-aligned in the strip reserved down the right-hand edge.
        private void OnTreeDrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null)
            {
                e.DrawDefault = true;
                return;
            }

            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            Color back = selected ? SystemColors.Highlight : _tree.BackColor;
            Color fore = selected ? SystemColors.HighlightText : _tree.ForeColor;
            const TextFormatFlags flags = TextFormatFlags.NoPrefix
                | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

            using (SolidBrush brush = new SolidBrush(back))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }
            TextRenderer.DrawText(e.Graphics, e.Node.Text, _tree.Font, e.Bounds, fore, flags);
            if (selected && (e.State & TreeNodeStates.Focused) != 0)
            {
                ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds);
            }

            string version = NodeVersion(e.Node);
            if (string.IsNullOrEmpty(version))
            {
                return;
            }
            // ClientSize already excludes the scrollbar, so the column stays put
            // whether or not the tree is long enough to need one.
            Rectangle column = new Rectangle(
                _tree.ClientSize.Width - VersionColumnWidth,
                e.Bounds.Top,
                VersionColumnWidth - VersionColumnGap,
                e.Bounds.Height);
            if (column.Left < e.Bounds.Right)
            {
                return; // a very long name has reached the column: the name wins
            }
            TextRenderer.DrawText(e.Graphics, version, _tree.Font, column,
                SystemColors.GrayText,
                flags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }

        // A plain button shows its macro's version on the button's own row. A
        // drop-down does not: its macros each carry their own, on the rows
        // underneath, and they need not agree.
        private static string NodeVersion(TreeNode node)
        {
            MacroCommand macro = node.Tag as MacroCommand;
            if (macro != null)
            {
                return macro.Version;
            }
            MacroButton button = node.Tag as MacroButton;
            if (button != null && !button.IsMulti && button.Macros.Count == 1)
            {
                return button.Macros[0].Version;
            }
            return null;
        }

        // Buttons from a switched-off library are hidden entirely rather than
        // greyed out - their settings are remembered, but an unusable row that
        // refuses to tick would only invite confusion.
        private IEnumerable<MacroButton> VisibleButtons()
        {
            return _buttons.Where(b => Settings.IsLibraryEnabled(_settings, b.LibraryPath));
        }

        private static string ButtonLabel(MacroButton button)
        {
            if (!button.IsMulti)
            {
                return button.Name;
            }
            int on = button.Macros.Count(m => m.Enabled);
            if (on == button.Macros.Count)
            {
                return button.Name + "   (" + button.Macros.Count + " macros)";
            }
            return button.Name + "   (" + on + " of " + button.Macros.Count + " macros)";
        }

        private void RefreshSkipped()
        {
            _skippedList.Items.Clear();
            foreach (SkippedFolder skipped in _skipped)
            {
                _skippedList.Items.Add(Path.GetFileName(skipped.Path.TrimEnd(
                    Path.DirectorySeparatorChar)) + "  -  " + skipped.Reason
                    + "      [" + skipped.Path + "]");
            }
            bool any = _skipped.Count > 0;
            _skippedLabel.Text = any
                ? "Not shown on the toolbar (" + _skipped.Count + ") - see Library > Guide for the folder layout:"
                : "";
            _skippedLabel.Visible = any;
            _skippedList.Visible = any;
            _layout.RowStyles[7].Height = any ? 74 : 0;
        }

        // ----- libraries -----

        private void OnAddLibrary(object sender, EventArgs e)
        {
            if (_settings.Libraries.Count >= Settings.MaxLibraries)
            {
                MessageBox.Show(this, "MacroDeck supports up to " + Settings.MaxLibraries + " libraries.",
                    "MacroDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string picked = FolderPicker.Show(null, "Select a macro library folder");
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }
            string normalized = picked.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string existing in _settings.Libraries)
            {
                string existingNorm = existing.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(existingNorm, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "That folder is already in the list.",
                        "MacroDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            _settings.Libraries.Add(normalized);
            RefreshAll();
        }

        private void OnRemoveLibrary(object sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show(this, "Select a library in the list first.",
                    "MacroDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int index = _grid.SelectedRows[0].Index;
            if (index < 0 || index >= _settings.Libraries.Count)
            {
                return;
            }
            string library = _settings.Libraries[index];
            DialogResult answer = MessageBox.Show(this,
                "Remove this library from MacroDeck?\r\n\r\n" + library +
                "\r\n\r\nThe folder and its macros are not deleted - only the toolbar buttons "
                + "and their saved settings.",
                "MacroDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }
            _settings.Libraries.RemoveAt(index);
            Settings.SetLibraryEnabled(_settings, library, true); // don't leave it in the disabled list

            string prefix = library.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            RemoveKeysUnder(_settings.Buttons, prefix);
            RemoveKeysUnder(_settings.Macros, prefix);
            RefreshAll();
        }

        private static void RemoveKeysUnder(Dictionary<string, ButtonPref> map, string prefix)
        {
            List<string> stale = map.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (string key in stale)
            {
                map.Remove(key);
            }
        }

        // Commit checkbox edits as soon as they are clicked rather than when
        // the cell loses focus.
        private void OnGridDirtyStateChanged(object sender, EventArgs e)
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void OnGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppressCheckEvents || e.ColumnIndex != LibraryEnabledColumn
                || e.RowIndex < 0 || e.RowIndex >= _settings.Libraries.Count)
            {
                return;
            }
            bool enabled = Convert.ToBoolean(_grid.Rows[e.RowIndex].Cells[LibraryEnabledColumn].Value);
            Settings.SetLibraryEnabled(_settings, _settings.Libraries[e.RowIndex], enabled);
            RefreshTree();
        }

        private void OnGridCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != LibraryScanColumn
                || e.RowIndex >= _settings.Libraries.Count)
            {
                return;
            }
            // Per-library scan: replace only that library's buttons and its
            // skipped-folder notes, leaving the other libraries untouched.
            string library = _settings.Libraries[e.RowIndex];
            _buttons.RemoveAll(b =>
                string.Equals(b.LibraryPath, library, StringComparison.OrdinalIgnoreCase));
            _skipped.RemoveAll(s => s.Path.StartsWith(library, StringComparison.OrdinalIgnoreCase));
            ScanResult rescan = LibraryScanner.Scan(library);
            SwpVersionReader.FillVersions(rescan.Buttons);
            _buttons.AddRange(rescan.Buttons);
            _skipped.AddRange(rescan.Skipped);
            _buttons = LibraryScanner.ApplyPrefs(_buttons, _settings);
            RefreshGrid();
            RefreshTree();
            RefreshSkipped();
            if (e.RowIndex < _grid.Rows.Count)
            {
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[1];
            }
        }

        // ----- checkboxes -----

        private void OnTreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_suppressCheckEvents || e.Node == null)
            {
                return;
            }
            if (e.Node.Level == 0)
            {
                MacroButton button = e.Node.Tag as MacroButton;
                if (button != null && button.FolderPath != null)
                {
                    button.Enabled = e.Node.Checked;
                    Settings.GetOrCreatePref(_settings, button.FolderPath).Enabled = e.Node.Checked;
                }
                return;
            }

            MacroCommand macro = e.Node.Tag as MacroCommand;
            if (macro == null || macro.FolderPath == null)
            {
                return;
            }
            macro.Enabled = e.Node.Checked;
            Settings.GetOrCreateMacroPref(_settings, macro.FolderPath).Enabled = e.Node.Checked;

            TreeNode parent = e.Node.Parent;
            MacroButton owner = parent != null ? parent.Tag as MacroButton : null;
            if (owner != null)
            {
                parent.Text = ButtonLabel(owner);
            }
        }

        // ----- drag-and-drop reordering (top-level nodes only) -----

        private void OnTreeItemDrag(object sender, ItemDragEventArgs e)
        {
            TreeNode node = e.Item as TreeNode;
            if (e.Button == MouseButtons.Left && node != null && node.Level == 0)
            {
                _dragNode = node;
                _tree.DoDragDrop(node, DragDropEffects.Move);
                _dragNode = null;
                SetInsertMark(null, false);
            }
        }

        private void OnTreeDragOver(object sender, DragEventArgs e)
        {
            if (_dragNode == null)
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            e.Effect = DragDropEffects.Move;
            TreeNode target;
            bool after;
            GetDropTarget(out target, out after);
            SetInsertMark(target, after);
        }

        private void OnTreeDragDrop(object sender, DragEventArgs e)
        {
            SetInsertMark(null, false);
            if (_dragNode == null)
            {
                return;
            }
            TreeNode target;
            bool after;
            GetDropTarget(out target, out after);
            if (target == null || target == _dragNode)
            {
                return;
            }
            int newIndex = target.Index + (after ? 1 : 0);
            int oldIndex = _dragNode.Index;
            if (newIndex > oldIndex)
            {
                newIndex--;
            }
            if (newIndex == oldIndex)
            {
                return;
            }
            TreeNode moving = _dragNode;
            _suppressCheckEvents = true;
            _tree.BeginUpdate();
            try
            {
                bool wasChecked = moving.Checked;
                bool wasExpanded = moving.IsExpanded;
                _tree.Nodes.Remove(moving);
                _tree.Nodes.Insert(newIndex, moving);
                moving.Checked = wasChecked;
                if (wasExpanded)
                {
                    moving.Expand();
                }
                _tree.SelectedNode = moving;
            }
            finally
            {
                _tree.EndUpdate();
                _suppressCheckEvents = false;
            }
            CaptureOrderFromTree();
        }

        private void OnTreeDragLeave(object sender, EventArgs e)
        {
            SetInsertMark(null, false);
        }

        // Maps the cursor position to a top-level target node and whether the
        // drop lands after it (bottom half) or before it (top half).
        private void GetDropTarget(out TreeNode target, out bool after)
        {
            Point point = _tree.PointToClient(Cursor.Position);
            TreeNode node = _tree.GetNodeAt(point);
            if (node == null)
            {
                target = _tree.Nodes.Count > 0 ? _tree.Nodes[_tree.Nodes.Count - 1] : null;
                after = true;
                return;
            }
            if (node.Level > 0)
            {
                // Over a macro row: treat it as the bottom of its button.
                target = node.Parent;
                after = true;
                return;
            }
            target = node;
            after = point.Y > node.Bounds.Top + (node.Bounds.Height / 2);
        }

        // The first drag freezes the arrangement: from then on the saved order
        // wins and newly found macros append at the end.
        private void CaptureOrderFromTree()
        {
            _settings.OrderCustomized = true;
            int order = 0;
            for (int i = 0; i < _tree.Nodes.Count; i++)
            {
                MacroButton button = _tree.Nodes[i].Tag as MacroButton;
                if (button != null && button.FolderPath != null)
                {
                    Settings.GetOrCreatePref(_settings, button.FolderPath).Order = order++;
                }
            }
            // Buttons hidden behind a switched-off library keep their saved
            // slots; push them past the visible ones so nothing collides.
            foreach (MacroButton hidden in _buttons.Where(
                b => !Settings.IsLibraryEnabled(_settings, b.LibraryPath)))
            {
                if (hidden.FolderPath != null)
                {
                    ButtonPref pref = Settings.GetOrCreatePref(_settings, hidden.FolderPath);
                    if (pref.Order < 0)
                    {
                        pref.Order = order++;
                    }
                }
            }
            _buttons = LibraryScanner.ApplyPrefs(_buttons, _settings);
        }

        private void OnSortAlphabetical(object sender, EventArgs e)
        {
            _settings.OrderCustomized = false;
            foreach (ButtonPref pref in _settings.Buttons.Values)
            {
                pref.Order = -1;
            }
            _buttons = LibraryScanner.ApplyPrefs(_buttons, _settings);
            RefreshTree();
        }

        // ----- Win32: drag insertion marker -----

        private const int TVM_SETINSERTMARK = 0x1100 + 26;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private void SetInsertMark(TreeNode node, bool after)
        {
            try
            {
                SendMessage(_tree.Handle, TVM_SETINSERTMARK,
                    (IntPtr)(after ? 1 : 0), node != null ? node.Handle : IntPtr.Zero);
            }
            catch { }
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
    }
}
