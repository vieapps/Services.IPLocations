#region Related components
using System;
using System.Xml;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Repository;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.IPLocations
{
	public class ServiceComponent : ServiceBase
	{
		public override string ServiceName => "IPLocations";

		IDisposable CacheCommunicator { get; set; }

		void RegisterCacheCommunicator()
		{
			this.CacheCommunicator?.Dispose();
			this.CacheCommunicator = Router.GotBackupRouter()
				? Router.BackupChannel.AssignProcessL1CacheRequest(Utility.Cache, this)
				: Router.IncomingChannel.AssignProcessL1CacheRequest(Utility.Cache, this);
			Utility.Cache.AssignSendL1CacheRequest(this, Router.GotBackupRouter());
		}

		public override Task RegisterServiceAsync(IEnumerable<string> args, Action<IService> onSuccess = null, Action<Exception> onError = null)
			=> base.RegisterServiceAsync
			(
				args,
				_ =>
				{
					this.RegisterCacheCommunicator();
					onSuccess?.Invoke(this);
				},
				onError
			);

		public override Task UnregisterServiceAsync(IEnumerable<string> args, bool available = true, Action<IService> onSuccess = null, Action<Exception> onError = null)
			=> base.UnregisterServiceAsync
			(
				args,
				available,
				_ =>
				{
					this.CacheCommunicator?.Dispose();
					this.CacheCommunicator = null;
					onSuccess?.Invoke(this);
				},
				onError
			);

		public override async Task StartAsync(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			// initialize
			this.Syncable = false;
			Utility.CancellationToken = this.CancellationToken;
			Utility.APIsURI = this.GetHttpURI("APIs", "https://apis.vieapps.net");
			await this.StartAsync(args, (_, _) => this.RegisterCacheCommunicator(), initializeRepository).ConfigureAwait(false);

			// configuration
			if (ConfigurationManager.GetSection("net.vieapps.services.iplocations.providers") is AppConfigurationSectionHandler svcConfig)
			{
				Utility.Providers = svcConfig.Section.SelectNodes("provider") is XmlNodeList svcProviders
					? svcProviders.ToList()
						.Select(svcProvider => new Provider(svcProvider.Attributes["name"]?.Value, svcProvider.Attributes["uriPattern"]?.Value, svcProvider.Attributes["accessKey"]?.Value ?? ""))
						.Where(provider => !string.IsNullOrWhiteSpace(provider.Name) && !string.IsNullOrWhiteSpace(provider.UriPattern))
						.ToDictionary(provider => provider.Name, provider => provider, StringComparer.OrdinalIgnoreCase)
					: [];

				var name = svcConfig.Section.Attributes["first"]?.Value ?? "ipstack";
				Utility.FirstProvider = Utility.Providers.TryGetValue(name, out Provider provider) ? provider : Utility.Providers.FirstOrDefault().Value;

				name = svcConfig.Section.Attributes["second"]?.Value ?? "ipapi";
				Utility.SecondProvider = Utility.Providers.TryGetValue(name, out provider) ? provider : Utility.Providers.FirstOrDefault().Value;

				Utility.SameLocationRegex = new Regex(svcConfig.Section.Attributes["sameLocationRegex"]?.Value ?? @"\d{1,3}\.\d{1,3}");
				Utility.SameLocationAddress = (svcConfig.Section.Attributes["sameLocationAddress"]?.Value ?? "127.0.0.1").ToList(";", true);
				Utility.ExternalURI = svcConfig.Section.Attributes["externalURI"]?.Value ?? Utility.APIsURI;
				Utility.DefaultLocation = svcConfig.Section.Attributes["default"]?.Value ?? "Hanoi, Vietnam";
			}

			// prepare at first run
			if (!string.IsNullOrWhiteSpace(Utility.ExternalURI) && !Utility.ExternalURI.IsEquals(Utility.APIsURI))
				try
				{
					await new Uri($"{Utility.ExternalURI}/discovery/services").FetchHttpAsync(this.CancellationToken).ConfigureAwait(false);
					this.Logger.LogInformation($"External APIs ({Utility.ExternalURI}) is working fine!");
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while fetching external APIs ({Utility.ExternalURI}) => {ex.Message}", ex);
					Utility.ExternalURI = null;
				}
			else
				Utility.ExternalURI = null;
			await Utility.PrepareAddressesAsync(this.CancellationToken, this.Logger).ConfigureAwait(false);

			// info
			this.Logger.LogInformation($"Providers: {string.Join(", ", Utility.Providers.Keys)}");
			this.Logger.LogInformation($"First provider: {Utility.FirstProvider?.Name ?? "N/A"}");
			this.Logger.LogInformation($"Second provider: {Utility.SecondProvider?.Name ?? "N/A"}");
			this.Logger.LogInformation($"Same Location (Regex): {Utility.SameLocationRegex}");
			this.Logger.LogInformation($"Same Location (Address): {Utility.SameLocationAddress.Join(" - ")}");
			this.Logger.LogInformation($"Public Address: {string.Join(" - ", Utility.PublicAddresses)}");
			this.Logger.LogInformation($"Local Address: {string.Join(" - ", Utility.LocalAddresses)}");

			// sync from others
			new CommunicateMessage(this.ServiceName)
			{
				Type = "Sync",
				ExcludedNodeID = this.NodeID
			}.Send();

			// current location
			try
			{
				await Task.Delay(UtilityService.GetRandomNumber(123, 456), this.CancellationToken).ConfigureAwait(false);
				Utility.CurrentLocation = await Utility.GetCurrentLocationAsync(this.Logger.LogError, this.CancellationToken).ConfigureAwait(false);
				this.Logger.LogInformation($"Current Location: {(Utility.CurrentLocation != null ? $"{Utility.CurrentLocation.City}, {Utility.CurrentLocation?.Region}, {Utility.CurrentLocation.Country}" : Utility.DefaultLocation)}");
			}
			catch (Exception ex)
			{
				this.Logger.LogError($"Error occurred while fetching current location => {ex.Message}", ex);
			}

			// clean (12 hours)
			this.StartTimer(async () =>
			{
				var userID = UtilityService.GetAppSetting("Users:SystemAccountID", "VIEAppsNGX-MMXVII-System-Account");
				var ipLocations = await IPLocation.FindAsync(Filters<IPLocation>.LessThan("LastUpdated", DateTime.Now.AddDays(-45)), null, 0, 1, null, this.CancellationToken).ConfigureAwait(false) ?? [];
				await ipLocations.ForEachAsync(async ipLocation =>
				{
					Utility.IPLocations.Remove(ipLocation.IP);
					await IPLocation.DeleteAsync<IPLocation>(ipLocation.ID, userID, this.CancellationToken).ConfigureAwait(false);
					new CommunicateMessage(this.ServiceName)
					{
						Type = "Remove",
						ExcludedNodeID = this.NodeID,
						Data = new JObject
						{
							["IP"] = ipLocation.IP
						}
					}.Send();
				}, true, false).ConfigureAwait(false);
			}, 12 * 60 * 60);

			// next step
			next?.Invoke(this);
		}

		protected override Task ProcessInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default)
		{
			if (message.Type.IsEquals("Update"))
				new IPLocation().Fill(message.Data, ipLocation => Utility.IPLocations[ipLocation.IP] = ipLocation);
			else if (message.Type.IsEquals("Remove"))
				Utility.IPLocations.Remove(message.Data.Get<string>("IP"));
			else if (message.Type.IsEquals("Sync"))
				Utility.IPLocations.Select(kvp => kvp.Value).ToList().ForEach(ipLocation => ipLocation.Send());
			return Task.CompletedTask;
		}

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			var stopwatch = Stopwatch.StartNew();
			await this.WriteLogsAsync(requestInfo, $"Begin request ({requestInfo.Verb} {requestInfo.GetURI()}{(string.IsNullOrWhiteSpace(requestInfo.GetObjectIdentity()) ? $"/{requestInfo.GetQueryParameter("ip") ?? requestInfo.GetQueryParameter("ip-address") ?? requestInfo.Session.IP}" : "")})").ConfigureAwait(false);
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.CancellationToken);

			try
			{
				if (!requestInfo.Verb.IsEquals("GET"))
					throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.GetURI()})");

				if (!string.IsNullOrWhiteSpace(requestInfo.ObjectName) && string.IsNullOrWhiteSpace(requestInfo.GetParameter("x-app-token")) && string.IsNullOrWhiteSpace(requestInfo.Session?.User?.ID) && (!requestInfo.TryGetParameter("x-requester", out var requester) || !requester.IsStartsWith("vieapps-ngx")))
					throw new TokenNotFoundException();

				JToken json = null;
				switch (requestInfo.ObjectName.ToLower())
				{
					case "local":
					case "localips":
					case "local-ips":
						json = Utility.LocalAddresses.Select(address => new JValue($"{address}")).ToJArray();
						break;

					case "public":
					case "publicips":
					case "public-ips":
					case "current":
					case "currentlocation":
					case "current-location":
						if (Utility.PublicAddresses.Count < 1)
							await Utility.PrepareAddressesAsync(this.CancellationToken, this.Logger, false).ConfigureAwait(false);
						json = requestInfo.ObjectName.IsStartsWith("current")
							? (Utility.CurrentLocation ?? (Utility.CurrentLocation = await Utility.GetCurrentLocationAsync(this.Logger.LogError, cts.Token).ConfigureAwait(false)) ?? new()).ToJson(ip => ip.Remove("LastUpdated"))
							: Utility.PublicAddresses.Select(address => new JValue($"{address}")).ToJArray();
						break;

					default:
						var ipAddress = requestInfo.GetQueryParameter("ip") ?? requestInfo.GetQueryParameter("ip-address") ?? requestInfo.Session.IP;
						json = string.IsNullOrWhiteSpace(ipAddress)
							? throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.GetURI()})")
							: (requestInfo.ContainsKey("x-use-external") && !string.IsNullOrWhiteSpace(Utility.ExternalURI)
								? await Utility.GetAsync(cts.Token, ipAddress).ConfigureAwait(false) ?? new()
								: ipAddress.IsSameLocation() && Utility.CurrentLocation != null
									? new IPLocation().CopyFrom(Utility.CurrentLocation, null, ipLocation =>
									{
										ipLocation.ID = ipAddress.GenerateUUID();
										ipLocation.IP = ipAddress;
									})
									: await Utility.GetLocationAsync(ipAddress, requestInfo.GetQueryParameter("x-provider"), requestInfo.ContainsKey("x-provider") || requestInfo.ContainsKey("x-use-internal"), (msg, ex) => this.WriteLogsAsync(requestInfo, msg, ex).Execute(), cts.Token).ConfigureAwait(false) ?? new()
							).ToJson(ip => ip.Remove("LastUpdated"));
						break;
				}

				stopwatch.Stop();
				await this.WriteLogsAsync(requestInfo, $"Success response - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
				if (this.IsDebugResultsEnabled)
					await this.WriteLogsAsync(requestInfo, $"- Request: {requestInfo.ToString(this.JsonFormat)}" + "\r\n" + $"- Response: {json?.ToString(this.JsonFormat)}").ConfigureAwait(false);

				return json;
			}
			catch (Exception ex)
			{
				throw this.GetRuntimeException(requestInfo, ex, stopwatch);
			}
		}
	}
}