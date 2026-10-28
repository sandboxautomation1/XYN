using System.Windows;

namespace GoldDesk.Desktop;

public partial class LoginWindow : Window
{
    private readonly GoldDeskService service;
    private bool signup;

    // Запазен е parameterless constructor за Visual Studio Designer.
    public LoginWindow() : this(new GoldDeskService())
    {
    }

    public LoginWindow(GoldDeskService service)
    {
        this.service = service;
        InitializeComponent();
    }

    private async void AuthButton_Click(object sender, RoutedEventArgs e)
    {
        AuthButton.IsEnabled = false;
        ToggleAuthButton.IsEnabled = false;
        AuthStatus.Text = "Момент…";

        try
        {
            var result = await service.LoginAsync(EmailBox.Text.Trim(), PasswordBox.Password, signup);
            if (!result.Ok)
            {
                AuthStatus.Text = result.Message;
                return;
            }

            if (signup && string.IsNullOrEmpty(result.Message))
            {
                AuthStatus.Text = "Проверете пощата си за потвърждение.";
                return;
            }

            var mainWindow = new MainWindow(service);
            Application.Current.MainWindow = mainWindow;
            mainWindow.Show();
            await mainWindow.InitializeAsync();
            Close();
        }
        catch (Exception ex)
        {
            AuthStatus.Text = "Възникна грешка: " + ex.Message;
        }
        finally
        {
            AuthButton.IsEnabled = true;
            ToggleAuthButton.IsEnabled = true;
        }
    }

    private void ToggleAuth_Click(object sender, RoutedEventArgs e)
    {
        signup = !signup;
        AuthTitle.Text = signup ? "Нов профил" : "Вход";
        AuthSubtitle.Text = signup
            ? "Създайте профил – данните ви са видими само за вас."
            : "Влезте, за да видите вашите сигнали и новини.";
        AuthButton.Content = signup ? "Създай профил" : "Влез";
        ToggleAuthButton.Content = signup ? "Вече имате профил? Вход" : "Нямате профил? Регистрация";
        AuthStatus.Text = "";
    }
}
