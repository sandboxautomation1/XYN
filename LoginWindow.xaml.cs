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
            AuthStatus.Text = "Неочаквана грешка: " + ex.Message;
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
        AuthTitle.Text = signup ? "Нова регистрация" : "Вход";
        AuthSubtitle.Text = signup
            ? "Създайте акаунт и потвърдете имейла си."
            : "Влезте, за да използвате пазарния анализ.";
        AuthButton.Content = signup ? "Създай акаунт" : "Вход";
        ToggleAuthButton.Content = signup ? "Вече имате акаунт? Вход" : "Нямате акаунт? Регистрация";
        AuthStatus.Text = "";
    }
}
