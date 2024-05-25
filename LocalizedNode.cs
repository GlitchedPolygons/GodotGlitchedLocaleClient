using System;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// This is a component that references a <see cref="LocalizationBucket"/>
/// and hooks into its dictionary to translate either a UI label or a button component's string value.
/// </summary>
public partial class LocalizedNode : Node
{
	public enum CaseTransform : int
	{
		None = 0,
		ToUppercase = 1,
		ToLowercase = 2
	}

	[Export]
	private LocalizationBucket localizationBucket;

	[Export]
	private Button button;

	[Export]
	private Label label;

	[Export]
	private string localizationKey = string.Empty;

	[Export]
	private string fallbackValue = string.Empty;

	[Export]
	private CaseTransform caseTransform = CaseTransform.None;

	public override void _Ready()
	{
		base._Ready();

		if (button is null)
		{
			button = GetParent() as Button;
		}

		if (label is null)
		{
			label = GetParent() as Label;
		}

#if DEBUG
		if (localizationBucket is null)
		{
			GD.PrintErr($"{nameof(LocalizedNode)}::{localizationBucket} is null. Assign the correct value to the field!");
		}
#endif
	}

	public override void _EnterTree()
	{
		base._EnterTree();

		localizationBucket.Refreshed += LocalizationBucketOnRefreshed;
		LocalizationBucket.ChangedLocale += LocalizationBucketOnChangedLocale;

		LocalizationBucketOnRefreshed();
	}

	public override void _ExitTree()
	{
		base._ExitTree();

		localizationBucket.Refreshed -= LocalizationBucketOnRefreshed;
		LocalizationBucket.ChangedLocale -= LocalizationBucketOnChangedLocale;
	}

	private async void LocalizationBucketOnRefreshed()
	{
		DateTime startUtc = DateTime.UtcNow;

		while (!localizationBucket.IsReady)
		{
			await Task.Delay(256);

#if DEBUG
			if (DateTime.UtcNow - startUtc > TimeSpan.FromSeconds(8))
			{
				GD.PrintErr($"Localization bucket \"{localizationBucket.BucketId}\" setup failed.");
			}
#endif
		}

		string newTranslation = localizationBucket[localizationKey];

		if (string.IsNullOrEmpty(newTranslation))
		{
			return;
		}

		newTranslation = caseTransform switch
		{
			CaseTransform.ToLowercase => newTranslation.ToLowerInvariant(),
			CaseTransform.ToUppercase => newTranslation.ToUpperInvariant(),
			_ => newTranslation
		};

		if (button is not null)
		{
			button.Text = newTranslation;
		}

		if (label is not null)
		{
			label.Text = newTranslation;
		}
	}

	private void LocalizationBucketOnChangedLocale(string newLocale)
	{
		LocalizationBucketOnRefreshed();
	}

	/// <summary>
	/// Checks whether or not this <see cref="LocalizedNode"/> is hooked into a specific instance of a <see cref="LocalizationBucket"/>.
	/// </summary>
	/// <param name="bucket">The bucket to check against.</param>
	/// <returns><paramref name="bucket"/> == <see cref="localizationBucket"/></returns>
	public bool IsInBucket(LocalizationBucket bucket)
	{
		return bucket == localizationBucket;
	}

	/// <summary>
	/// Gets the localization key configured in this <see cref="LocalizedNode"/> instance.
	/// </summary>
	/// <returns><see cref="localizationKey"/></returns>
	public string GetLocalizationKey()
	{
		return localizationKey;
	}
}
