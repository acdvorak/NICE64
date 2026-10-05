namespace PicoSwitchController;

public sealed class VideoPreviewForm : Form
{
    private readonly PictureBox _pictureBox = new()
    {
        BackColor = Color.Black,
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom
    };

    public VideoPreviewForm()
    {
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        Text = "SNES Capture Preview";
        WindowState = FormWindowState.Maximized;
        Controls.Add(_pictureBox);

        _pictureBox.DoubleClick += (_, _) => Close();
    }

    public void ShowOnCurrentScreen(Form owner)
    {
        Screen screen = Screen.FromControl(owner);
        Bounds = screen.Bounds;
        Show(owner);
        Activate();
    }

    public void SetFrame(Bitmap bitmap)
    {
        Image? previous = _pictureBox.Image;
        _pictureBox.Image = bitmap;
        previous?.Dispose();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Image? previous = _pictureBox.Image;
        _pictureBox.Image = null;
        previous?.Dispose();
        base.OnFormClosed(e);
    }
}
