using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace instplayer;

public class App : Application
{
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.3.0")]
	public void InitializeComponent()
	{
		base.StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
	}

	private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		try
		{
			System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jip_error.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
		}
		catch
		{
		}
		try
		{
			MessageBox.Show("오류가 발생했습니다 / An error occurred:\n" + e.Exception.Message, "JIP", MessageBoxButton.OK, MessageBoxImage.Error);
		}
		catch
		{
		}
		e.Handled = true;
	}

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.3.0")]
	public static void Main()
	{
		App app = new App();
		app.DispatcherUnhandledException += app.App_DispatcherUnhandledException;
		app.InitializeComponent();
		app.Run();
	}
}
