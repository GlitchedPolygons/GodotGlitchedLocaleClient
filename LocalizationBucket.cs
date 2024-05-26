using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;
using FileAccess = Godot.FileAccess;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// A localization bucket is a component that contains a dictionary of localized key-value string pairs for a list of user-defined locales.<para> </para>
/// It connects to an instance of the Glitched Locale Server to allow users to change translations at runtime without requiring to submit a new package update/release everytime a typo is corrected...<para> </para>
/// <seealso cref="LocalizedNode"/>
/// More information available on:
/// https://glitchedpolygons.com/store/software/glitched-locale-server
/// </summary>
public partial class LocalizationBucket : Node
{
	/// <summary>
	/// DTO for requesting translations.
	/// </summary>
	protected sealed class TranslationRequestDto
	{
		/// <summary>
		/// Glitched Locale Server User ID.
		/// </summary>
		public string UserId { get; set; }

		/// <summary>
		/// [OPTIONAL] Read-access password (if the user account matching above <see cref="UserId"/> has one set up).
		/// </summary>
		public string ReadAccessPassword { get; set; }

		/// <summary>
		/// [OPTIONAL] Unix-timestamp of when we requested translations for this <see cref="LocalizationBucket"/> for the last time. <para> </para>
		/// This is to avoid over-fetching data and reduce bandwidth if possible (ideally, the backend only ever returns stuff that's changed since the last fetch).
		/// </summary>
		public long? LastFetchUTC { get; set; }

		/// <summary>
		/// The keys of all the translations you want to download from the localization server.
		/// </summary>
		public List<string> Keys { get; set; }

		/// <summary>
		/// The list of locales for which to download the translations.
		/// </summary>
		public List<string> Locales { get; set; }
	}

	/// <summary>
	/// Response DTO for the translation endpoint's response body item type.
	/// </summary>
	protected sealed class TranslationEndpointResponseDto
	{
		/// <summary>
		/// Translation key.
		/// </summary>
		[JsonPropertyName("key")]
		public string Key { get; set; }

		/// <summary>
		/// Dictionary of translations containing the locale as dictionary key, and the corresponding localized string as value.
		/// </summary>
		[JsonPropertyName("translations")]
		public System.Collections.Generic.Dictionary<string, string> Translations { get; set; }
	}

	/// <summary>
	/// Error structure to return in response bodies.
	/// </summary>
	protected sealed class Error
	{
		/// <summary>
		/// The error code (clients can switch on this number to for example pick a corresponding translated error message).
		/// </summary>
		public int Code { get; set; }

		/// <summary>
		/// Raw server-side error message to the client (in English).
		/// </summary>
		public string Message { get; set; }

		/// <summary>
		/// Parameterless ctor.
		/// </summary>
		public Error()
		{
			//nop
		}

		/// <summary>
		/// Creates an <see cref="Error"/> using a specific error code and message.
		/// </summary>
		/// <param name="code">Internal API error code.</param>
		/// <param name="message">Server-side error message (in English).</param>
		public Error(int code, string message)
		{
			Code = code;
			Message = message;
		}
	}

	/// <summary>
	/// If <see cref="Items"/> is not <c>null</c> or empty, it means that the request was successful. <para> </para>
	/// Failed requests should return the correct HTTP status code, but (if applicable) still return one or more errors inside <see cref="Errors"/>.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	protected sealed class ResponseBodyDto<T>
	{
		/// <summary>
		/// The type of the items returned. Can be <c>null</c> if the request was successful but nothing's returned (e.g. a status <c>201</c>).
		/// </summary>
		[JsonPropertyName("type")]
		public string Type { get; set; } = null;

		/// <summary>
		/// The total amount of items potentially available to fetch.
		/// </summary>
		[JsonPropertyName("count")]
		public long Count { get; set; } = 0;

		/// <summary>
		/// This and <see cref="Errors"/> are mutually exclusive: if this is set, <see cref="Errors"/> should be <c>null</c> and vice-versa!<para> </para>
		/// This can be <c>null</c> if the request succeeded and there are no items to return.
		/// </summary>
		[JsonPropertyName("items")]
		public T[] Items { get; set; } = null;

		/// <summary>
		/// If the request failed, one or potentially more errors CAN be written into this array for the client to handle.
		/// </summary>
		[JsonPropertyName("errors")]
		public Error[] Errors { get; set; } = null;

		/// <summary>
		/// Parameterless ctor.
		/// </summary>
		public ResponseBodyDto()
		{
			//nop
		}

		/// <summary>
		/// Spawn an error response.
		/// </summary>
		/// <param name="errors">One or more errors.</param>
		/// <seealso cref="Error"/>
		public ResponseBodyDto(params Error[] errors)
		{
			Errors = errors;
		}
	}

	/// <summary>
	/// Default base URL that points to the official Glitched Polygons Locale Server.
	/// Its frontend is reachable under: https://locales.glitchedpolygons.com
	/// </summary>
	public const string DEFAULT_LOCALE_SERVER_BASE_URL = "https://api.locales.glitchedpolygons.com";

	/// <summary>
	/// Default endpoint path to use for fetching translations from the server.
	/// </summary>
	public const string DEFAULT_LOCALE_SERVER_TRANSLATION_ENDPOINT = "/api/v1/translations/translate";

	/// <summary>
	/// This event is raised when the <see cref="LocalizationBucket"/> refreshed its dictionary of translations.<para> </para>
	/// Interested scripts should subscribe to this event in order to know that they should refresh their labels/UI/usages of the translations.
	/// </summary>
	public event Action Refreshed;

	/// <summary>
	/// This event is raised whenever the <see cref="SetLocale"/> method is called, such that interested subscribers know when to refresh their labels in the UI to reflect the new language setting.
	/// </summary>
	public static event Action<string> ChangedLocale;

	/// <summary>
	/// This event is raised when a connection attempt to the locale server resulted in a failure.
	/// </summary>
	public event Action FailedConnectionToLocaleServer;

	/// <summary>
	/// This is <c>true</c> as soon as Godot's <see cref="_Ready"/> method has finished executing.
	/// </summary>
	public bool IsReady { get; private set; }

	public bool RefreshNeeded => cache.IsEmpty || UtcNow - lastFetchUTC > minSecondsBetweenRequests;

	public string BucketId => bucketId;

	[Export]
	private string bucketId = string.Empty;

	[Export]
	private string userId = string.Empty;

	[Export(PropertyHint.Password)]
	private string apiKey = string.Empty;

	[Export(PropertyHint.Password)]
	private string readAccessPassword = string.Empty;

	[Export]
	private string localeServerBaseUrl = DEFAULT_LOCALE_SERVER_BASE_URL;

	[Export]
	private string localeServerTranslationEndpoint = DEFAULT_LOCALE_SERVER_TRANSLATION_ENDPOINT;

	[Export(PropertyHint.Range, "300,345600,")]
	private int minSecondsBetweenRequests = 86400;

	[Export(PropertyHint.Range, "64,8192,")]
	private int maxRefreshResponseTimeMilliseconds = 4096;

	[Export]
	private bool returnTranslationKeyWhenNotFound = false;

	[Export]
	private string configIdLocaleIndex = "locale_index";

	[Export]
	private string configIdLastFetchUTC = "last_fetch_utc";

	[Export]
	private string localizationCacheDirectoryName = "localization_cache";

	/// <summary>
	/// The config file name where current locale index and localization relevant data will be stored.
	/// </summary>
	[Export]
	private string configFileName = "config.json";

	[Export]
	private bool saveLocalizationCacheToDiskOnNodeExitTree = false;

	[Export]
	private bool saveLocalizationCacheToDiskOnQuit = false;

	[Export]
	private bool verbose = true;

	[Export]
	private Array<string> locales = new()
	{
		"en_US.UTF-8",
		"de_DE.UTF-8",
		"it_IT.UTF-8",
	};

	[Export]
	private Array<string> keys;

	private int localeIndex = 0;

	private string cacheDirectory = null;

	private ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache = new();

	private static System.Collections.Generic.Dictionary<string, long> config = null;

	private long lastFetchUTC = 0;

	private bool refreshing = false;

	private readonly HttpClient httpClient = new();

	private readonly JsonSerializerOptions prettyPrint = new()
	{
		WriteIndented = true
	};

	private long UtcNow => new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
	private long UtcNowMs => new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();

	public override void _Ready()
	{
		cacheDirectory = $"user://{localizationCacheDirectoryName}";

		DirAccess dir = DirAccess.Open(cacheDirectory);

		if (dir is null)
		{
			DirAccess.MakeDirAbsolute(cacheDirectory);

			dir = DirAccess.Open(cacheDirectory);
		}

		bool saveConfig = false;

		if (config is null && dir.FileExists(configFileName))
		{
			using FileAccess configFile = FileAccess.Open($"{cacheDirectory}/{configFileName}", FileAccess.ModeFlags.Read);

			try
			{
				config = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, long>>(configFile.GetAsText());
			}
			catch (Exception e)
			{
				saveConfig = true;
#if DEBUG
				GD.PrintErr($"Failed to deserialize localization config file \"{configFileName}\" from disk. Thrown exception: {e.ToString()}");
#endif
			}
		}
		else saveConfig = true;

		config ??= new System.Collections.Generic.Dictionary<string, long>(2);

		config.TryAdd(configIdLocaleIndex, 0);
		config.TryAdd($"{configIdLastFetchUTC}_{bucketId}", 0);

		lastFetchUTC = config[$"{configIdLastFetchUTC}_{bucketId}"];
		SetLocale(locales[(int)config[configIdLocaleIndex]], saveConfig);

		httpClient.BaseAddress = new Uri(localeServerBaseUrl);

		LoadCacheFromDisk();

		dir = null;
		IsReady = true;

		Refresh();
	}

	private void OnChangedLocale(string newLocale)
	{
		int newLocaleIndex = locales.IndexOf(newLocale);

		if (newLocaleIndex != localeIndex)
		{
			SetLocale(newLocale, false);
		}
	}

	public override void _EnterTree()
	{
		base._EnterTree();

		ChangedLocale += OnChangedLocale;
	}

	public override void _ExitTree()
	{
		base._ExitTree();

		ChangedLocale -= OnChangedLocale;
		
		if (saveLocalizationCacheToDiskOnNodeExitTree)
		{
			WriteCacheToDisk();
		}
	}
	
	/// <summary>
	/// Checks whether or not the locale server defined in the <see cref="LocalizationBucket"/>'s base URL field is online and reachable.
	/// </summary>
	/// <returns><see cref="Task"/> - Use <see cref="HttpResponseMessage"/>.<see cref="HttpResponseMessage.IsSuccessStatusCode"/> to find out whether or not the server is reachable. The returned response body furthermore contains the server's public RSA key.</returns>
	public Task<HttpResponseMessage> IsLocaleServerReachable()
	{
		return Task.Run(() => httpClient.GetAsync("/api/v1/keys/rsa/public"));
	}

	/// <summary>
	/// Useful indexer for getting a translation from the bucket directly with the square brackets operator.
	/// </summary>
	/// <param name="key">Translation key.</param>
	public string this[string key] => Translate(key);

	/// <summary>
	/// Gets the translated string value for a specific translation key.<para> </para>
	/// Note that translation keys must be in the <see cref="LocalizationBucket"/>'s list of translation keys in order for them to be fetched from the locale server!<para> </para>
	/// Use the <see cref="LocalizationBucket"/>'s inspector for that: there is a context menu action that scans the scene for missing keys. Use it every time you added new translation usages to your scene to prevent untranslated values in the final product!
	/// </summary>
	/// <param name="key">Translation key.</param>
	/// <returns><c>null</c> if the translation couldn't be found in the <see cref="LocalizationBucket"/>'s dictionary; the translated string value otherwise.</returns>
	public string Translate(string key)
	{
		if (RefreshNeeded && !refreshing)
		{
			Refresh();
		}

		if (!cache.TryGetValue(key, out ConcurrentDictionary<string, string> translations))
		{
			return returnTranslationKeyWhenNotFound ? key : null;
		}

		if (!translations.TryGetValue(locales[localeIndex], out string translation))
		{
			return returnTranslationKeyWhenNotFound ? key : null;
		}

		return translation;
	}

	public void LoadCacheFromDisk()
	{
		using FileAccess cacheFile = FileAccess.Open($"{cacheDirectory}/{bucketId}", FileAccess.ModeFlags.Read);

		if (cacheFile is null)
		{
#if DEBUG
			if (verbose)
			{
				GD.Print($"Localization cache file for bucket \"{bucketId}\" not found: fetching & refreshing translations now...\n");
			}
#endif
			Refresh();
		}
		else
		{
			using MemoryStream fileStream = new(cacheFile.GetBuffer((long)cacheFile.GetLength()));
			using BrotliStream brotli = new(fileStream, CompressionMode.Decompress);
			using MemoryStream memoryStream = new();

			brotli.CopyTo(memoryStream);

			string json = Encoding.UTF8.GetString(memoryStream.ToArray());

			cache = JsonSerializer.Deserialize<ConcurrentDictionary<string, ConcurrentDictionary<string, string>>>(json);
			
#if DEBUG
			if (verbose)
			{
				GD.Print($"Localization bucket \"{bucketId}\" loaded from cache file on disk:\n\n{JsonSerializer.Serialize(cache)}\n");
			}
#endif
		}

		Refreshed?.Invoke();
	}

	/// <summary>
	/// Forces the localization cache to be written to disk immediately.
	/// </summary>
	public void WriteCacheToDisk()
	{
		using MemoryStream bytes = new(1024);
		using BrotliStream brotli = new(bytes, CompressionLevel.Optimal, false);

		string json = JsonSerializer.Serialize(cache);

#if DEBUG
		if (verbose)
		{
			GD.Print($"Writing localization cache for bucket \"{bucketId}\" to disk:\n\n{json}\n");
		}
#endif

		brotli.Write(Encoding.UTF8.GetBytes(json));
		brotli.Flush();

		using FileAccess cacheFile = FileAccess.Open($"{cacheDirectory}/{bucketId}", FileAccess.ModeFlags.Write);

		cacheFile.StoreBuffer(bytes.ToArray());
	}

	/// <summary>
	/// Change the locale server to fetch translations from. This will trigger a <see cref="Refresh"/>!
	/// Make sure that the new server you want to point the <see cref="LocalizationBucket"/> to is online and reachable!
	/// </summary>
	/// <param name="newLocaleServerBaseUrl">The new base URL </param>
	/// <param name="translationEndpoint"></param>
	/// <returns><c>true</c> if the server was changed, <c>false</c> if no changes were made.</returns>
	public bool ChangeServer(string newLocaleServerBaseUrl = DEFAULT_LOCALE_SERVER_BASE_URL, string translationEndpoint = DEFAULT_LOCALE_SERVER_TRANSLATION_ENDPOINT)
	{
		if (newLocaleServerBaseUrl == localeServerBaseUrl && translationEndpoint == localeServerTranslationEndpoint)
		{
			return false;
		}

		localeServerBaseUrl = newLocaleServerBaseUrl;
		localeServerTranslationEndpoint = translationEndpoint;
		httpClient.BaseAddress = new Uri(localeServerBaseUrl);
		Refresh();

		return true;
	}

	/// <summary>
	/// Gets the list of currently enabled locales in the <see cref="LocalizationBucket"/>
	/// </summary>
	/// <returns><see cref="IReadOnlyCollection{String}"/></returns>
	public IReadOnlyCollection<string> GetLocales()
	{
		return locales;
	}

	/// <summary>
	/// Changes the <see cref="LocalizationBucket"/>'s active locale setting.<para> </para>
	/// The <see cref="ChangedLocale"/> event will be raised and all the <see cref="LocalizedText"/>s in the scene
	/// will need to refresh their UI labels as well as all the other scripts that make use of translations from this bucket. 
	/// </summary>
	/// <param name="locale">The new locale to use (e.g. <c>en_US.UTF-8</c>). This value must be in the list of enabled locales (use <see cref="GetLocales"/> to find out which locales are currently enabled in the <see cref="LocalizationBucket"/>).</param>
	/// <param name="save">Set this to <c>false</c> if you don't wish to save the locale modification to disk.</param>
	/// <returns>Whether or not the locale change was successfully applied.</returns>
	public bool SetLocale(string locale, bool save = true)
	{
		localeIndex = locales.IndexOf(locale);

		if (localeIndex == -1)
		{
			return false;
		}

		config[configIdLocaleIndex] = localeIndex;

		ChangedLocale?.Invoke(locale);

		if (save)
		{
			WriteConfigToDisk();
		}

		return true;
	}

	private void WriteConfigToDisk()
	{
		using FileAccess configFile = FileAccess.Open($"{cacheDirectory}/{configFileName}", FileAccess.ModeFlags.Write);

		configFile.StoreString(JsonSerializer.Serialize(config, prettyPrint));
	}

	private async void Refresh()
	{
		if (!IsReady || keys.Count == 0)
		{
			return;
		}

		if (!RefreshNeeded)
		{
#if DEBUG
			if (verbose)
			{
				GD.Print($"Refreshing of localization bucket \"{bucketId}\" cancelled because it's still fresh. {UtcNow - lastFetchUTC} seconds have passed since the last fetch op (minimum amount of seconds between refreshes is {minSecondsBetweenRequests}).\n");
			}
#endif
			return;
		}

		if (refreshing)
		{
#if DEBUG
			if (verbose)
			{
				GD.Print($"Refreshing of localization bucket \"{bucketId}\" cancelled because it's already refreshing...\n");
			}
#endif
			return;
		}

#if DEBUG
		if (verbose)
		{
			GD.Print($"Refreshing localization bucket \"{bucketId}\" now...\n");
		}
#endif
		refreshing = true;

		long? storedLastFetchUTC = lastFetchUTC;

		lastFetchUTC = UtcNow;

		config[$"{configIdLastFetchUTC}_{bucketId}"] = lastFetchUTC;

		Task task = Task.Run(async () =>
		{
			TranslationRequestDto dto = new()
			{
				UserId = userId,
				Keys = keys.ToList(),
				Locales = locales.ToList(),
				ReadAccessPassword = readAccessPassword,
				LastFetchUTC = storedLastFetchUTC,
			};

			string requestDtoJson = JsonSerializer.Serialize(dto);

			using StringContent httpContent = new(requestDtoJson, Encoding.UTF8, "application/json");

			if (!string.IsNullOrEmpty(apiKey))
			{
				httpContent.Headers.Add("API-Key", apiKey);
			}

			HttpResponseMessage response = await httpClient.PostAsync(localeServerTranslationEndpoint, httpContent);

			if (!response.IsSuccessStatusCode)
			{
				return;
			}

			string json = await response.Content.ReadAsStringAsync();

			ResponseBodyDto<TranslationEndpointResponseDto> responseBodyDto = JsonSerializer.Deserialize<ResponseBodyDto<TranslationEndpointResponseDto>>(json);

#if DEBUG
			if (verbose)
			{
				GD.Print($"Locale server response body for localization bucket \"{bucketId}\":\n\n{json}\n");
				GD.Print($"Deserialized locale server response for localization bucket \"{bucketId}\":\n\n{JsonSerializer.Serialize(responseBodyDto)}\n");
			}
#endif

			if (responseBodyDto != null)
			{
				bool save = false;

				foreach (TranslationEndpointResponseDto translation in responseBodyDto.Items)
				{
					if (cache.TryGetValue(translation.Key, out ConcurrentDictionary<string, string> cachedTranslation))
					{
						foreach ((string key, string value) in translation.Translations)
						{
							cachedTranslation[key] = value;
						}
					}
					else
					{
						cache[translation.Key] = new ConcurrentDictionary<string, string>(translation.Translations);
					}

					save = true;
				}

				if (save)
				{
					WriteCacheToDisk();
				}
			}
		});

		while (!task.IsCompleted)
		{
			await Task.Delay(256);

			if
			(
				UtcNowMs - (lastFetchUTC * 1000) > maxRefreshResponseTimeMilliseconds
			)
			{
				FailedConnectionToLocaleServer?.Invoke();
				refreshing = false;
				return;
			}
		}

		refreshing = false;
		Refreshed?.Invoke();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
		{
			if (saveLocalizationCacheToDiskOnQuit)
			{
				WriteCacheToDisk();
			}

			config[configIdLocaleIndex] = localeIndex;

			WriteConfigToDisk();

			GetTree().Quit();
		}
	}
}
