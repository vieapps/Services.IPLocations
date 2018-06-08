#region Related components
using System;
using System.Linq;
using System.Net;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.IPLocations
{
	public class ServiceComponent : ServiceBase
	{
		public override string ServiceName => "IPLocations";

		public override void Start(string[] args = null, bool initializeRepository = true, Func<ServiceBase, Task> nextAsync = null)
			=> base.Start(args, initializeRepository, async (service) =>
			{
				// prepare
				await Utility.PrepareAddressesAsynnc().ConfigureAwait(false);
				Utility.GetProviders();

				// last action
				if (nextAsync != null)
					try
					{
						await nextAsync(service).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.Logger.LogError("Error occurred while invoking the next action", ex);
					}

				this.Logger.LogInformation($"Providers: {string.Join(", ", Utility.Providers.Keys)}");
				this.Logger.LogInformation($"First provider: {Utility.FirstProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Second provider: {Utility.SecondProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Public Address: {string.Join(" - ", Utility.PublicAddresses)}");
				this.Logger.LogInformation($"Local Address: {string.Join(" - ", Utility.LocalAddresses)}");
				this.Logger.LogInformation($"Same Location Regex: {Utility.SameLocationRegex}");
			});

		public override async Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			var stopwatch = Stopwatch.StartNew();
			this.Logger.LogInformation($"Begin request ({requestInfo.Verb} {requestInfo.URI}) [{requestInfo.CorrelationID}]");
			try
			{
				// check
				if (!requestInfo.Verb.IsEquals("GET"))
					throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.URI})");

				// prepare
				var ipAddress = requestInfo.GetQueryParameter("ip-address");
				if (string.IsNullOrWhiteSpace(ipAddress))
					throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.URI})");

				// check to see the same location
				if (Utility.IsSameLocation(ipAddress))
					return new IPLocation
					{
						ID = ipAddress.GetMD5(),
						IP = ipAddress,
						City = "N/A",
						Region = "N/A",
						Country = "N/A",
						CountryCode = "N/A",
						Continent = "N/A",
						Latitude = "N/A",
						Longitude = "N/A",
					}.ToJson();

				// find in database
				var json = (await IPLocation.GetAsync<IPLocation>(ipAddress.GetMD5(), cancellationToken).ConfigureAwait(false))?.ToJson();

				// no recorded, then request to provider
				if (json == null)
					json = await this.GetAsync(ipAddress, cancellationToken).ConfigureAwait(false);

				stopwatch.Stop();
				this.Logger.LogInformation($"Success response - Execution times: {stopwatch.GetElapsedTimes()} [{requestInfo.CorrelationID}]");
				if (this.IsDebugResultsEnabled)
					this.Logger.LogInformation(
						$"- Request: {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}" + "\r\n" +
						$"- Response: {json?.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
					);
				return json;
			}
			catch (Exception ex)
			{
				throw this.GetRuntimeException(requestInfo, ex, stopwatch);
			}
		}

		async Task<JObject> GetAsync(string ipAddress, CancellationToken cancellationToken, string correlationID = null)
		{
			try
			{
				return await this.GetAsync(Utility.FirstProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception fe)
			{
				await this.WriteLogsAsync(correlationID ?? UtilityService.NewUUID, $"Error while processing with \"{Utility.FirstProvider?.Name}\" provider: {fe.Message}", fe).ConfigureAwait(false);
				try
				{
					return await this.GetAsync(Utility.SecondProvider?.Name, ipAddress, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception se)
				{
					await this.WriteLogsAsync(correlationID ?? UtilityService.NewUUID, $"Error while processing with \"{Utility.SecondProvider?.Name}\" provider: {se.Message}", se).ConfigureAwait(false);
					return new IPLocation
					{
						ID = ipAddress.GetMD5(),
						IP = ipAddress,
						City = "N/A",
						Region = "N/A",
						Country = "N/A",
						CountryCode = "N/A",
						Continent = "N/A",
						Latitude = "N/A",
						Longitude = "N/A",
					}.ToJson();
				}
			}
		}

		Task<JObject> GetAsync(string providerName, string ipAddress, CancellationToken cancellationToken)
		{
			switch ((providerName ?? "ipstack").ToLower())
			{
				case "ipapi":
					return this.GetByIpApiAsync(ipAddress, cancellationToken);

				case "ipstack":
				default:
					return this.GetByIpStackAsync(ipAddress, cancellationToken);
			}
		}

		async Task<JObject> GetByIpStackAsync(string ipAddress, CancellationToken cancellationToken)
		{
			// get IP address from provider
			var json = JObject.Parse(await new WebClient().DownloadStringTaskAsync(Utility.Providers["ipstack"].GetUrl(ipAddress), cancellationToken).ConfigureAwait(false));

			// update database
			var ipLocation = new IPLocation
			{
				ID = json.Get<string>("ip").GetMD5(),
				IP = json.Get<string>("ip"),
				City = json.Get<string>("city"),
				Region = json.Get<string>("region_name"),
				Country = json.Get<string>("country_name"),
				CountryCode = json.Get<string>("country_code"),
				Continent = json.Get<string>("continent_name"),
				Latitude = json.Get<string>("latitude"),
				Longitude = json.Get<string>("longitude"),
			};
			await IPLocation.CreateAsync(ipLocation, cancellationToken).ConfigureAwait(false);

			// response
			return ipLocation.ToJson();
		}

		async Task<JObject> GetByIpApiAsync(string ipAddress, CancellationToken cancellationToken)
		{
			// get IP address from provider
			var json = JObject.Parse(await new WebClient().DownloadStringTaskAsync(Utility.Providers["ipapi"].GetUrl(ipAddress), cancellationToken).ConfigureAwait(false));

			// update database
			var continent = json.Get<string>("timezone");
			var ipLocation = new IPLocation
			{
				ID = json.Get<string>("query").GetMD5(),
				IP = json.Get<string>("query"),
				City = json.Get<string>("city"),
				Region = json.Get<string>("regionName"),
				Country = json.Get<string>("country"),
				CountryCode = json.Get<string>("countryCode"),
				Continent = continent.Left(continent.IndexOf("/")),
				Latitude = json.Get<string>("lat"),
				Longitude = json.Get<string>("lon"),
			};
			await IPLocation.CreateAsync(ipLocation, cancellationToken).ConfigureAwait(false);

			// response
			return ipLocation.ToJson();
		}
	}
}