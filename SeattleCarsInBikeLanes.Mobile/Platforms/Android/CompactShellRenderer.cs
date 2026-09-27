using Google.Android.Material.BottomNavigation;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;

namespace SeattleCarsInBikeLanes.Mobile.Platforms.Android;

internal sealed class CompactShellRenderer : ShellRenderer
{
	protected override IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(
		ShellItem shellItem) => new CompactBottomNavAppearanceTracker(this, shellItem);

	private sealed class CompactBottomNavAppearanceTracker : ShellBottomNavViewAppearanceTracker
	{
		private BottomNavigationView? bottomView;

		public CompactBottomNavAppearanceTracker(IShellContext context, ShellItem shellItem)
			: base(context, shellItem)
		{
		}

		public override void SetAppearance(BottomNavigationView bottomView, IShellAppearanceElement appearance)
		{
			base.SetAppearance(bottomView, appearance);
			TrackView(bottomView);
		}

		public override void ResetAppearance(BottomNavigationView bottomView)
		{
			base.ResetAppearance(bottomView);
			TrackView(bottomView);
		}

		private void TrackView(BottomNavigationView view)
		{
			if (bottomView != view)
			{
				if (bottomView is not null)
				{
					bottomView.LayoutChange -= OnLayoutChange;
				}

				bottomView = view;
				bottomView.LayoutChange += OnLayoutChange;
			}

			UpdateLayout();
		}

		private void OnLayoutChange(object? sender, global::Android.Views.View.LayoutChangeEventArgs e)
		{
			if (e.Right - e.Left != e.OldRight - e.OldLeft)
			{
				UpdateLayout();
			}
		}

		private void UpdateLayout()
		{
			if (bottomView?.Resources is not { } resources)
			{
				return;
			}

			// Shell's native container rotates without reliably raising Shell.SizeChanged.
			bool landscape = bottomView.RootView is { } root && root.Width > root.Height;
			int Dimension(int portrait, int compact) =>
				resources.GetDimensionPixelSize(landscape ? compact : portrait);

			// Leave view padding alone: Material owns the gesture/navigation-bar and cutout insets.
			bottomView.SetMinimumHeight(Dimension(
				Resource.Dimension.shell_tab_bar_height, Resource.Dimension.compact_shell_tab_bar_height));
			bottomView.ItemPaddingTop = Dimension(
				Resource.Dimension.shell_tab_bar_padding_top, Resource.Dimension.compact_shell_tab_bar_padding);
			bottomView.ItemPaddingBottom = Dimension(
				Resource.Dimension.shell_tab_bar_padding_bottom, Resource.Dimension.compact_shell_tab_bar_padding);
			bottomView.ItemActiveIndicatorHeight = Dimension(
				Resource.Dimension.shell_tab_bar_indicator_height, Resource.Dimension.compact_shell_tab_bar_indicator_height);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && bottomView is not null)
			{
				bottomView.LayoutChange -= OnLayoutChange;
				bottomView = null;
			}

			base.Dispose(disposing);
		}
	}
}
