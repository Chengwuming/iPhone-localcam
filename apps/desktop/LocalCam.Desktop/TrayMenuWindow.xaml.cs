using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace LocalCam.Desktop;

public partial class TrayMenuWindow : Window
{
    private readonly Action showLocalCam;
    private readonly Action connectIphone;
    private readonly Action showSettings;
    private readonly Action exit;
    private bool hasActivated;

    public TrayMenuWindow(
        Action showLocalCam,
        Action connectIphone,
        Action showSettings,
        Action exit)
    {
        this.showLocalCam = showLocalCam;
        this.connectIphone = connectIphone;
        this.showSettings = showSettings;
        this.exit = exit;
        InitializeComponent();
        Loaded += (_, _) => PositionNearCursor();
        Activated += (_, _) => hasActivated = true;
        Deactivated += async (_, _) =>
        {
            await Task.Delay(150);
            if (hasActivated && !IsActive && !IsMouseOver)
            {
                Close();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    private void PositionNearCursor()
    {
        var cursor = Forms.Cursor.Position;
        var source = PresentationSource.FromVisual(this);
        var screenPoint = source?.CompositionTarget?.TransformFromDevice.Transform(
            new System.Windows.Point(cursor.X, cursor.Y)) ?? new System.Windows.Point(cursor.X, cursor.Y);
        var workArea = SystemParameters.WorkArea;

        Left = Math.Clamp(screenPoint.X - ActualWidth + 18, workArea.Left + 8, workArea.Right - ActualWidth - 8);
        Top = Math.Clamp(screenPoint.Y - ActualHeight - 10, workArea.Top + 8, workArea.Bottom - ActualHeight - 8);
    }

    private void Execute(Action action)
    {
        Close();
        action();
    }

    private void ShowLocalCam_Click(object sender, RoutedEventArgs e) => Execute(showLocalCam);

    private void ConnectIphone_Click(object sender, RoutedEventArgs e) => Execute(connectIphone);

    private void Settings_Click(object sender, RoutedEventArgs e) => Execute(showSettings);

    private void Exit_Click(object sender, RoutedEventArgs e) => Execute(exit);
}
