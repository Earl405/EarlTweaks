using System.Windows;

namespace LiveBoost;

public partial class LoginWindow : Window
{
    bool _show;

    public LoginWindow()
    {
        InitializeComponent();
        if (!CloudConfig.IsConfigured)
            ErrorText.Text = "Cloud sign-in is not configured yet. Set the Firebase values in CloudConfig.cs and rebuild.";
        Loaded += (_, _) => UserBox.Focus();
    }

    bool AdminPortal => AdminLoginRb.IsChecked == true;

    void Portal_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;   // Checked fires while the XAML is still being built, before later controls exist
        Heading.Text = AdminPortal ? "Admin login" : "User login";
        SubHeading.Text = AdminPortal ? "For administrators only. Sign in to manage user accounts."
                                      : "Use the username and password provided by your administrator.";
        Title = AdminPortal ? "LiveBoost - Admin sign in" : "LiveBoost - User sign in";
        ErrorText.Text = CloudConfig.IsConfigured ? "" : ErrorText.Text;
        UserBox.Clear(); PwdBox.Clear(); PwdText.Clear();
        UserBox.Focus();
    }

    string Pwd => _show ? PwdText.Text : PwdBox.Password;

    void Show_Click(object sender, RoutedEventArgs e)
    {
        _show = !_show;
        if (_show) { PwdText.Text = PwdBox.Password; PwdText.Visibility = Visibility.Visible; PwdBox.Visibility = Visibility.Collapsed; PwdText.Focus(); }
        else { PwdBox.Password = PwdText.Text; PwdText.Visibility = Visibility.Collapsed; PwdBox.Visibility = Visibility.Visible; PwdBox.Focus(); }
        ShowBtn.Content = _show ? "Hide" : "Show";
    }

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var user = UserBox.Text.Trim();
        var pwd = Pwd;

        if (user.Length == 0 || pwd.Length == 0) { ErrorText.Text = "Enter your username and password."; return; }

        ActionBtn.IsEnabled = false; Busy.Visibility = Visibility.Visible;
        var role = AdminPortal ? "Admin" : "User";   // read the control here on the UI thread, not inside Task.Run
        var res = await Task.Run(() => Auth.Login(user, pwd, role));
        if (res.Ok) { DialogResult = true; return; }

        ErrorText.Text = res.Error;
        await Task.Delay(600);                       // slows down guessing
        PwdBox.Clear(); PwdText.Clear(); ConfirmBox.Clear();
        Busy.Visibility = Visibility.Collapsed; ActionBtn.IsEnabled = true;
        (_show ? (UIElement)PwdText : PwdBox).Focus();
    }

    void Exit_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
}
