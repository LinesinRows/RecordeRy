using System;
using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace RecordeRy.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(
        StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException +=
            OnCurrentDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        System.Windows.MessageBox.Show(
            $"Beklenmeyen bir hata oluştu:\n\n{e.Exception.Message}",
            "RecordeRy",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Beklenmeyen bir hata oluştu:\n\n{ex.Message}",
                "RecordeRy",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    protected override void OnExit(
        ExitEventArgs e)
    {
        base.OnExit(e);
    }
}

