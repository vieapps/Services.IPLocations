#region Related components
using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.IPLocations
{
	public class ServiceComponent : ServiceBase
	{
		public override string ServiceName => "IPLocations";

		public override async Task StartAsync(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			// initialize
			this.Syncable = false;
			Utility.CancellationToken = this.CancellationToken;
			Utility.Cache = new Components.Caching.Cache($"VIEApps-Services-{this.ServiceName}", Components.Utility.Logger.GetLoggerFactory());
			Utility.ExternalURI = UtilityService.GetAppSetting("IPLocations:External");
			await base.StartAsync(args, initializeRepository).ConfigureAwait(false);

			// test external
			if (!string.IsNullOrWhiteSpace(Utility.ExternalURI))
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

			// prepare at first run
			await Utility.PrepareAddressesAsync(this.CancellationToken, this.Logger).ConfigureAwait(false);

			this.Logger.LogInformation($"Providers: {string.Join(", ", Utility.Providers.Keys)}");
			this.Logger.LogInformation($"First provider: {Utility.FirstProvider?.Name ?? "N/A"}");
			this.Logger.LogInformation($"Second provider: {Utility.SecondProvider?.Name ?? "N/A"}");
			this.Logger.LogInformation($"Expression of Same Location (Regex): {Utility.SameLocationRegex}");
			this.Logger.LogInformation($"Expression of Same Location (Address): {Utility.SameLocationAddress.Join(", ")}");
			this.Logger.LogInformation($"Public Address: {string.Join(" - ", Utility.PublicAddresses)}");
			this.Logger.LogInformation($"Local Address: {string.Join(" - ", Utility.LocalAddresses)}");

			try
			{
				Utility.CurrentLocation = await Utility.GetCurrentLocationAsync(this.Logger, this.CancellationToken).ConfigureAwait(false);
				this.Logger.LogInformation($"Current Location: {(Utility.CurrentLocation != null ? $"{Utility.CurrentLocation.City}, {Utility.CurrentLocation?.Region}, {Utility.CurrentLocation.Country}" : UtilityService.GetAppSetting("IPLocations:Default", "N/A"))}");
			}
			catch (Exception ex)
			{
				this.Logger.LogError($"Error occurred while fetching current location => {ex.Message}", ex);
			}

			// next step
			next?.Invoke(this);
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
							await Utility.PrepareAddressesAsync(this.CancellationToken, this.Logger, false, false).ConfigureAwait(false);
						json = requestInfo.ObjectName.IsStartsWith("current")
							? (Utility.CurrentLocation ?? (Utility.CurrentLocation = await Utility.GetCurrentLocationAsync(this.Logger, cts.Token, requestInfo.Session.User.ID).ConfigureAwait(false)) ?? new()).ToJson(ip => ip.Remove("LastUpdated"))
							: Utility.PublicAddresses.Select(address => new JValue($"{address}")).ToJArray();
						break;

					default:
						var ipAddress = requestInfo.GetQueryParameter("ip") ?? requestInfo.GetQueryParameter("ip-address") ?? requestInfo.Session.IP;
						json = string.IsNullOrWhiteSpace(ipAddress)
							? throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.GetURI()})")
							: (requestInfo.ContainsKey("x-use-external") && !string.IsNullOrWhiteSpace(Utility.ExternalURI)
								? await Utility.GetAsync(cts.Token, ipAddress).ConfigureAwait(false) ?? new()
								: ipAddress.IsSameLocation()
									? new IPLocation().CopyFrom(Utility.CurrentLocation, null, ipLocation =>
										{
											ipLocation.ID = ipAddress.GenerateUUID();
											ipLocation.IP = ipAddress;
										})
									: await Utility.GetLocationAsync(ipAddress, this.Logger, requestInfo.Session.User.ID, cts.Token).ConfigureAwait(false) ?? new()
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