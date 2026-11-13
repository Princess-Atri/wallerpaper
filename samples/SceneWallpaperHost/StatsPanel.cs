using System;
using System.Drawing;
using System.Windows.Forms;

namespace SceneWallpaperHost;

/// <summary>
/// Optional topmost, click-through diagnostics panel. It is intentionally a
/// separate layered WinForms window so it never affects the D3D11 swap chain.
/// </summary>
internal sealed class StatsPanel : IDisposable
{
    private readonly PanelForm _form;
    private readonly Label _label;
    private bool _visible;

    public StatsPanel()
    {
        _label = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(210, 0, 0, 0),
            ForeColor = Color.FromArgb(230, 220, 255),
            Font = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            Padding = new Padding(10, 8, 10, 8),
            Text = "Scene stats"
        };

        _form = new PanelForm
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(24, 24),
            Size = new Size(390, 180),
            TopMost = true,
            ShowInTaskbar = false,
            BackColor = Color.Black,
            Opacity = 0.86,
            Text = "Wallerpaper Scene Stats"
        };
        _form.Controls.Add(_label);
        _form.Show();
        _visible = true;
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible)
        {
            _form.Show();
            _form.BringToFront();
        }
        else
        {
            _form.Hide();
        }
    }

    public void Toggle() => SetVisible(!_visible);

    public void Update(string text)
    {
        if (!_visible)
            return;
        if (_label.Text != text)
            _label.Text = text;
    }

    public void Dispose()
    {
        _form.Hide();
        _form.Dispose();
    }

    private sealed class PanelForm : Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (click-through)
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }
    }
}
