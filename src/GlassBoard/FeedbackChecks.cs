using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassBoard;

public static class FeedbackChecks
{
	private sealed class Probe : GlassWindow
	{
		public Probe()
			: base(22.0)
		{
		}

		public void CheckTransparency()
		{
			Width = 260.0;
			Height = 200.0;
			CurrentMaterial = new MaterialOptions
			{
				Gloss = 1.0,
				Grain = 1.0,
				Shadow = true
			};
			int[] array = new int[4] { 0, 1, 2, 3 };
			foreach (int kind in array)
			{
				CurrentMaterial.Kind = kind;
				SetGlassOpacity(0.0);
				if (Surface.Effect != null || Surface.BorderBrush != Brushes.Transparent || Surface.Background != Brushes.Transparent || Root.Children.OfType<Border>().Any((Border b) => b.Tag as string == "material" && b.Opacity != 0.0))
				{
					throw new Exception("玻璃 100% 透明仍有底色、阴影或纹理");
				}
				SetGlassOpacity(72.0);
				if (Surface.Background == Brushes.Transparent)
				{
					throw new Exception("材质恢复失败");
				}
			}
		}
	}

	public static void Run()
	{
		Probe probe = new Probe();
		try
		{
			probe.CheckTransparency();
		}
		finally
		{
			probe.Close();
		}
		Controller controller = new Controller(desktop: false);
		WidgetManager widgetManager = null;
		try
		{
			controller.Board.CheckResizeRefresh();
			controller.Prefs.Widgets.HideControls = true;
			controller.Prefs.Widgets.Clock = true;
			controller.Prefs.Widgets.Weather = false;
			controller.Prefs.Widgets.Music = false;
			controller.Prefs.Widgets.Notes = false;
			widgetManager = new WidgetManager(controller);
			widgetManager.Sync();
			WidgetWindow widgetWindow = widgetManager.SnapshotWindows.FirstOrDefault();
			if (widgetWindow == null || widgetWindow.ContextMenu == null || widgetWindow.ContextMenu.Items.Count == 0)
			{
				throw new Exception("隐藏按钮后缺少右键恢复入口");
			}
		}
		finally
		{
			widgetManager?.Dispose();
			controller.ShuttingDown = true;
			controller.Dock.ReleaseHotkey();
			controller.Board.Close();
			controller.Dock.Close();
		}
	}
}
