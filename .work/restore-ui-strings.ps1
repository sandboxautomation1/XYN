$enc = [Text.UTF8Encoding]::new($false)
function Set-Lines($path, $changes) {
    $lines = [IO.File]::ReadAllLines((Join-Path (Get-Location) $path), $enc)
    foreach ($item in $changes.GetEnumerator()) { $lines[[int]$item.Key - 1] = $item.Value }
    [IO.File]::WriteAllLines((Join-Path (Get-Location) $path), $lines, $enc)
}
Set-Lines 'LoginWindow.xaml' @{
  4='        Title="Gold Desk · Вход"'
  22='                    <TextBlock x:Name="AuthTitle" Text="Вход" FontSize="26" Margin="0,0,0,4"/>'
  23='                    <TextBlock x:Name="AuthSubtitle" Text="Влезте, за да използвате пазарния анализ." Foreground="{StaticResource MutedBrush}" TextWrapping="Wrap" Margin="0,0,0,16"/>'
  28='                    <Button x:Name="AuthButton" Content="Вход" Click="AuthButton_Click"/>'
  29='                    <Button x:Name="ToggleAuthButton" Content="Нямате акаунт? Регистрация" Click="ToggleAuth_Click" Background="Transparent" Foreground="{StaticResource MutedBrush}"/>'
}
Set-Lines 'LoginWindow.xaml.cs' @{
  50='            AuthStatus.Text = "Неочаквана грешка: " + ex.Message;'
  62='        AuthTitle.Text = signup ? "Нова регистрация" : "Вход";'
  64='            ? "Създайте акаунт и потвърдете имейла си."'
  65='            : "Влезте, за да използвате пазарния анализ.";'
  66='        AuthButton.Content = signup ? "Създай акаунт" : "Вход";'
  67='        ToggleAuthButton.Content = signup ? "Вече имате акаунт? Вход" : "Нямате акаунт? Регистрация";'
}
Set-Lines 'MainWindow.xaml' @{
  18='                <TextBlock x:Name="LastUpdatedText" Text="Последно обновяване · market data" Foreground="{StaticResource MutedBrush}" FontSize="11"/>'
  24='                        <TextBox x:Name="SymbolInputBox" Width="80" TextAlignment="Center" Margin="0,4,6,4" Text="XAUUSD.p" ToolTip="Въведете MT5 символ, например XAUUSD.p"/>'
  32='                <Button x:Name="ChangeSourceButton" Content="Стартирай Bridge" Click="StartMt5Bridge_Click"/>'
  100='                                <TextBlock Text="Следващите 24 часа" Foreground="{StaticResource MutedBrush}" FontSize="10" Margin="0,3,0,8"/>'
  102='                                <TextBlock x:Name="ShortTermAverageText" Text="Средно: --" Foreground="{StaticResource MutedBrush}" FontSize="10" Margin="0,5,0,0"/>'
  111='                                <TextBlock Text="Следващата седмица" Foreground="{StaticResource MutedBrush}" FontSize="10" Margin="0,3,0,8"/>'
  113='                                <TextBlock x:Name="LongTermAverageText" Text="Средно: --" Foreground="{StaticResource MutedBrush}" FontSize="10" Margin="0,5,0,0"/>'
  132='                            <TextBlock x:Name="DailyAverageText" Text="Средно: --" Foreground="{StaticResource TextBrush}" FontSize="10" Margin="0,5,0,0"/>'
  138='                            <TextBlock x:Name="WeeklyAverageText" Text="Средно: --" Foreground="{StaticResource TextBrush}" FontSize="10" Margin="0,5,0,0"/>'
  224='                    <TabItem Header="Сигнал" Foreground="Black">'
  334='                                <CheckBox x:Name="DefaultNewsBox" Content="Зареждай автоматично Google News новини" IsChecked="True"/>'
}
$path = Join-Path (Get-Location) 'MainWindow.xaml.cs'
$s = [IO.File]::ReadAllText($path, $enc)
$s = $s.Replace(([char]0xFFFD).ToString(), ' · ')
[IO.File]::WriteAllText($path, $s, $enc)
