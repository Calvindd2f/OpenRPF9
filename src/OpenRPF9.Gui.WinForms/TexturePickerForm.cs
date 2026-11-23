using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace OpenRPF9
{
    internal sealed class TexturePickerForm : Form
    {
        private readonly IList<RageTextureInfo> textures;
        private readonly TextBox searchBox;
        private readonly ListView list;
        private readonly Label countLabel;
        private readonly Button okBtn;
        private int chosen;

        public int SelectedIndex { get { return chosen; } }

        public TexturePickerForm(IList<RageTextureInfo> textures, int initialIndex)
        {
            this.textures = textures ?? new List<RageTextureInfo>();
            this.chosen = initialIndex;

            Text = "Select Texture";
            Width = 780;
            Height = 580;
            MinimumSize = new Size(520, 320);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            try { Icon = Program.LoadAppIcon(); } catch { }

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 36;
            top.Padding = new Padding(8, 8, 8, 4);

            Label searchLabel = new Label();
            searchLabel.Text = "Search";
            searchLabel.AutoSize = true;
            searchLabel.Left = 8;
            searchLabel.Top = 10;
            top.Controls.Add(searchLabel);

            searchBox = new TextBox();
            searchBox.Left = 60;
            searchBox.Top = 7;
            searchBox.Width = 690;
            searchBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            searchBox.TextChanged += delegate { ApplyFilter(); };
            searchBox.KeyDown += SearchBoxKeyDown;
            top.Controls.Add(searchBox);

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.GridLines = true;
            list.Columns.Add("#", 56, HorizontalAlignment.Right);
            list.Columns.Add("Name", 340);
            list.Columns.Add("Size", 110);
            list.Columns.Add("Format", 120);
            list.Columns.Add("Mips", 60, HorizontalAlignment.Right);
            list.Columns.Add("Shape", 120);
            list.DoubleClick += delegate { Accept(); };
            list.KeyDown += ListKeyDown;

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 44;
            bottom.Padding = new Padding(8, 8, 8, 8);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Right;
            buttons.Width = 190;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;

            Button cancelBtn = new Button();
            cancelBtn.Text = "Cancel";
            cancelBtn.Width = 84;
            cancelBtn.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancelBtn);

            okBtn = new Button();
            okBtn.Text = "OK";
            okBtn.Width = 84;
            okBtn.Enabled = false;
            okBtn.Click += delegate { Accept(); };
            buttons.Controls.Add(okBtn);

            countLabel = new Label();
            countLabel.Dock = DockStyle.Fill;
            countLabel.TextAlign = ContentAlignment.MiddleLeft;

            bottom.Controls.Add(countLabel);
            bottom.Controls.Add(buttons);

            list.SelectedIndexChanged += delegate { okBtn.Enabled = list.SelectedItems.Count > 0; };

            Controls.Add(list);
            Controls.Add(top);
            Controls.Add(bottom);
            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            ActiveControl = searchBox;

            ApplyFilter();
            SelectInitial(initialIndex);
        }

        private void SearchBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && list.Items.Count > 0)
            {
                list.Focus();
                if (list.SelectedItems.Count == 0) { list.Items[0].Selected = true; list.Items[0].Focused = true; }
                e.Handled = true;
            }
        }

        private void ListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
        }

        private void Accept()
        {
            if (list.SelectedItems.Count == 0) return;
            object tag = list.SelectedItems[0].Tag;
            if (tag is int) chosen = (int)tag;
            DialogResult = DialogResult.OK;
            Close();
        }

        private bool Matches(RageTextureInfo t, string needle)
        {
            if (needle.Length == 0) return true;
            if (!string.IsNullOrEmpty(t.Name) && t.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(t.FormatName) && t.FormatName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if ((t.Width + "x" + t.Height).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void ApplyFilter()
        {
            string needle = (searchBox.Text ?? "").Trim();
            list.BeginUpdate();
            list.Items.Clear();
            try
            {
                for (int i = 0; i < textures.Count; i++)
                {
                    RageTextureInfo t = textures[i];
                    if (!Matches(t, needle)) continue;
                    ListViewItem item = new ListViewItem(i.ToString());
                    item.SubItems.Add(string.IsNullOrEmpty(t.Name) ? "(unnamed)" : t.Name);
                    item.SubItems.Add(t.Width + "x" + t.Height);
                    item.SubItems.Add(string.IsNullOrEmpty(t.FormatName) ? "?" : t.FormatName);
                    item.SubItems.Add(t.MipCount.ToString());
                    item.SubItems.Add(t.Shape);
                    item.Tag = i;
                    list.Items.Add(item);
                }
            }
            finally { list.EndUpdate(); }

            countLabel.Text = needle.Length == 0
                ? textures.Count + " textures"
                : list.Items.Count + " of " + textures.Count + " textures match";
        }

        private void SelectInitial(int initialIndex)
        {
            if (list.Items.Count == 0) return;
            ListViewItem pick = list.Items[0];
            foreach (ListViewItem item in list.Items)
            {
                if (item.Tag is int && (int)item.Tag == initialIndex) { pick = item; break; }
            }
            pick.Selected = true;
            pick.Focused = true;
            list.EnsureVisible(pick.Index);
        }
    }
}
