using Android.App;
using Android.Content;
using Android.Content.Res;
using Google.Android.Material.Color;
using SeattleCarsInBikeLanes.Mobile.Resources.Styles;
using MauiColor = Microsoft.Maui.Graphics.Color;

namespace SeattleCarsInBikeLanes.Mobile.Platforms.Android;

internal static class AndroidMaterial3Theme
{
	private static readonly MaterialToken[] Tokens =
	[
		new("M3Primary", Resource.Attribute.colorPrimary),
		new("M3OnPrimary", Resource.Attribute.colorOnPrimary),
		new("M3PrimaryContainer", Resource.Attribute.colorPrimaryContainer),
		new("M3OnPrimaryContainer", Resource.Attribute.colorOnPrimaryContainer),
		new("M3Secondary", Resource.Attribute.colorSecondary),
		new("M3OnSecondary", Resource.Attribute.colorOnSecondary),
		new("M3SecondaryContainer", Resource.Attribute.colorSecondaryContainer),
		new("M3OnSecondaryContainer", Resource.Attribute.colorOnSecondaryContainer),
		new("M3Tertiary", Resource.Attribute.colorTertiary),
		new("M3OnTertiary", Resource.Attribute.colorOnTertiary),
		new("M3TertiaryContainer", Resource.Attribute.colorTertiaryContainer),
		new("M3OnTertiaryContainer", Resource.Attribute.colorOnTertiaryContainer),
		new("M3Error", Resource.Attribute.colorError),
		new("M3OnError", Resource.Attribute.colorOnError),
		new("M3ErrorContainer", Resource.Attribute.colorErrorContainer),
		new("M3OnErrorContainer", Resource.Attribute.colorOnErrorContainer),
		new("M3Surface", Resource.Attribute.colorSurface),
		new("M3SurfaceContainer", Resource.Attribute.colorSurfaceContainer),
		new("M3SurfaceVariant", Resource.Attribute.colorSurfaceVariant),
		new("M3OnSurface", Resource.Attribute.colorOnSurface),
		new("M3OnSurfaceVariant", Resource.Attribute.colorOnSurfaceVariant),
		new("M3Outline", Resource.Attribute.colorOutline),
		new("M3OutlineVariant", Resource.Attribute.colorOutlineVariant)
	];

	internal static void Apply(Activity activity)
	{
		Microsoft.Maui.Controls.Application application =
			Microsoft.Maui.Controls.Application.Current
			?? throw new InvalidOperationException("The MAUI application is not available.");

		Material3Colors colors = application.Resources.MergedDictionaries
			.OfType<Material3Colors>()
			.SingleOrDefault()
			?? throw new InvalidOperationException("The Android Material 3 color resources are not loaded.");

		Context themedContext = DynamicColors.WrapContextIfAvailable(activity);
		int[] attributes = Tokens.Select(token => token.Attribute).ToArray();
		TypedArray values = themedContext.ObtainStyledAttributes(attributes);

		try
		{
			for (int index = 0; index < Tokens.Length; index++)
			{
				MaterialToken token = Tokens[index];
				if (!values.HasValue(index))
				{
					throw new InvalidOperationException(
						$"The Android theme does not define the Material 3 attribute for {token.Key}.");
				}

				colors[token.Key] = ToMauiColor(values.GetColor(index, 0));
			}
		}
		finally
		{
			values.Recycle();
		}
	}

	private static MauiColor ToMauiColor(int value)
	{
		global::Android.Graphics.Color color = new global::Android.Graphics.Color(value);
		return MauiColor.FromRgba(color.R, color.G, color.B, color.A);
	}

	private readonly record struct MaterialToken(string Key, int Attribute);
}
