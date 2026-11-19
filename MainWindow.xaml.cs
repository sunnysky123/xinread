using System;
using Microsoft.UI.Xaml;

namespace xinread
{
	/// <summary>
	/// The application window. This hosts a Frame that displays pages.
	/// </summary>
	public sealed partial class MainWindow : Window
	{
		public MainWindow()
		{
			InitializeComponent();

			ExtendsContentIntoTitleBar = true;
			SetTitleBar(AppTitleBar);

			AppWindow.SetIcon("Assets/AppIcon.ico");
		}

		/// <summary>把根 Frame 导航到指定页面。</summary>
		public void NavigateTo(Type pageType) => RootFrame.Navigate(pageType);
	}
}
