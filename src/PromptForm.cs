using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClaudeSessionDeck
{
    /// <summary>A small dark dialog that asks for one line of text.</summary>
    internal static class PromptForm
    {
        /// <summary>Returns the entered text, or null when the user cancels.</summary>
        public static string Ask(IWin32Window owner, string title, string current)
        {
            using (Form form = new Form())
            using (TextBox box = new TextBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                form.Text = title;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = false;
                form.TopMost = true;
                form.ClientSize = new Size(360, 96);
                form.BackColor = Theme.Background;
                form.ForeColor = Theme.Text;
                form.Font = new Font(Theme.Mono, 10f);

                box.SetBounds(14, 16, 332, 24);
                box.Text = current;
                box.BackColor = Theme.Hover;
                box.ForeColor = Theme.Text;
                box.BorderStyle = BorderStyle.FixedSingle;
                box.SelectAll();

                ok.Text = "OK";
                ok.SetBounds(186, 54, 78, 28);
                ok.DialogResult = DialogResult.OK;
                ok.FlatStyle = FlatStyle.Flat;
                cancel.Text = "Cancel";
                cancel.SetBounds(270, 54, 78, 28);
                cancel.DialogResult = DialogResult.Cancel;
                cancel.FlatStyle = FlatStyle.Flat;

                form.Controls.AddRange(new Control[] { box, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
