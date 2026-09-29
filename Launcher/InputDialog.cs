using PeakRelay.Launcher.Core;

namespace PeakRelay.Launcher;

/// <summary>Minimal input dialog used for the GitHub token prompt (masked input).</summary>
public static class InputDialog
{
    public static bool Show(IWin32Window? owner, string title, string prompt,
        bool usePassword, ref string? value)
    {
        using var form = new Form
        {
            Text = title,
            Width = 560,
            Height = 170,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
        };
        var label = new Label { Text = prompt, Dock = DockStyle.Top, Padding = new Padding(10, 10, 10, 4), AutoSize = true };
        var box = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = usePassword };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        form.Controls.Add(box);
        form.Controls.Add(label);
        form.Controls.Add(buttons);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        var result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
        if (result == DialogResult.OK)
        {
            value = box.Text;
            return true;
        }
        return false;
    }
}
