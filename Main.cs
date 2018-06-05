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
		public ServiceComponent() : base() { }

		public override string ServiceName => "IPLocations";

		#region Start
		public override void Start(string[] args = null, bool initializeRepository = true, Func<ServiceBase, Task> nextAsync = null)
		{
			base.Start(args, initializeRepository, async (service) =>
			{
				// prepare
				await Utility.PrepareAddressesAsynnc().ConfigureAwait(false);
				Utility.GetProviders();
				this.Logger.LogInformation($"First provider: {Utility.FirstProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Second provider: {Utility.SecondProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Local address: {string.Join(" - ", Utility.LocalAddresses)}");
				this.Logger.LogInformation($"Public address: {string.Join(" - ", Utility.PublicAddresses)}");

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
			});
		}
		#endregion

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
				var ip = requestInfo.GetQueryParameter("ip");
				if (IPAddress.IsLoopback(IPAddress.Parse(ip)))
					return new IPLocation
					{
						ID = ip.GetMD5(),
						IP = ip,
						City = "Local",
						Region = "N/A",
						Country = "N/A",
						CountryCode = "N/A",
						Continent = "N/A",
						Latitude = "N/A",
						Longitude = "N/A",
					}.ToJson();

				if (ip.IndexOf(".") > 0)
				{
					var address = ip.ToArray('.').Take(0, 3).Join(".");
					foreach (var ipAddress in Utility.LocalAddresses)
					{
						var adr = $"{ipAddress}".ToArray('.').Take(0, 3).Join(".");
						if (address.Equals(adr))
							return new IPLocation
							{
								ID = ip.GetMD5(),
								IP = ip,
								City = "Local",
								Region = "N/A",
								Country = "N/A",
								CountryCode = "N/A",
								Continent = "N/A",
								Latitude = "N/A",
								Longitude = "N/A",
							}.ToJson();
					}
				}

				// find in database
				var json = (await IPLocation.GetAsync<IPLocation>(ip.GetMD5(), cancellationToken).ConfigureAwait(false))?.ToJson();

				// no recorded, then request to provider
				if (json == null)
					json = await this.GetAsync(ip, cancellationToken).ConfigureAwait(false);

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

		async Task<JObject> GetAsync(string ip, CancellationToken cancellationToken)
		{
			try
			{
				return await this.GetAsync(Utility.FirstProvider?.Name, ip, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception fe)
			{
				await this.WriteLogsAsync(UtilityService.NewUUID, $"Error while processing with \"{Utility.FirstProvider?.Name}\" provider: {fe.Message}", fe).ConfigureAwait(false);
				try
				{
					return await this.GetAsync(Utility.SecondProvider?.Name, ip, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception se)
				{
					await this.WriteLogsAsync(UtilityService.NewUUID, $"Error while processing with \"{Utility.SecondProvider?.Name}\" provider: {se.Message}", se).ConfigureAwait(false);
					return new IPLocation
					{
						ID = ip.GetMD5(),
						IP = ip,
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

		Task<JObject> GetAsync(string providerName, string ip, CancellationToken cancellationToken)
		{
			switch (providerName.ToLower())
			{
				case "ipapi":
					return this.GetByIpApiAsync(ip, cancellationToken);

				case "ipstack":
				default:
					return this.GetByIpStackAsync(ip, cancellationToken);
			}
		}

		async Task<JObject> GetByIpStackAsync(string ip, CancellationToken cancellationToken)
		{
			// send request
			var provider = Utility.Providers["ipstack"];
			var url = provider.UriPattern.Replace(StringComparison.OrdinalIgnoreCase, "{ip}", ip).Replace(StringComparison.OrdinalIgnoreCase, "{accessKey}", provider.AccessKey);
			var json = JObject.Parse(await UtilityService.GetWebPageAsync(url, null, null, cancellationToken).ConfigureAwait(false));

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
			json = ipLocation.ToJson();
			return json;
		}

		async Task<JObject> GetByIpApiAsync(string ip, CancellationToken cancellationToken)
		{
			// send request
			var provider = Utility.Providers["ipapi"];
			var url = provider.UriPattern.Replace(StringComparison.OrdinalIgnoreCase, "{ip}", ip).Replace(StringComparison.OrdinalIgnoreCase, "{accessKey}", provider.AccessKey);
			var json = JObject.Parse(await UtilityService.GetWebPageAsync(url, null, null, cancellationToken).ConfigureAwait(false));

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
			json = ipLocation.ToJson();
			return json;
		}
	}
}