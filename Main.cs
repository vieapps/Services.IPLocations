﻿#region Related components
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

		public override void Start(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			// initialize caching storage
			Utility.Cache = new Components.Caching.Cache($"VIEApps-Services-{this.ServiceName}", Components.Utility.Logger.GetLoggerFactory());

			// start the service
			base.Start(args, initializeRepository, async _ =>
			{
				// prepare
				await Utility.PrepareAddressesAsync(this.CancellationTokenSource.Token, this.Logger).ConfigureAwait(false);
				var currentLocation = await Utility.GetCurrentLocationAsync(this.CancellationTokenSource.Token, this.Logger).ConfigureAwait(false);

				this.Logger.LogInformation($"Providers: {string.Join(", ", Utility.Providers.Keys)}");
				this.Logger.LogInformation($"First provider: {Utility.FirstProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Second provider: {Utility.SecondProvider?.Name ?? "N/A"}");
				this.Logger.LogInformation($"Public Address: {string.Join(" - ", Utility.PublicAddresses)}");
				this.Logger.LogInformation($"Local Address: {string.Join(" - ", Utility.LocalAddresses)}");
				this.Logger.LogInformation($"Current Location: {currentLocation.City}, {currentLocation.Region}, {currentLocation.Country}");
				this.Logger.LogInformation($"Expression of Same Location (Regex): {Utility.SameLocationRegex}");

				// last action
				next?.Invoke(this);
			});
		}

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			var stopwatch = Stopwatch.StartNew();
			this.WriteLogs(requestInfo, $"Begin request ({requestInfo.Verb} {requestInfo.GetURI()})");
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.CancellationTokenSource.Token))
				try
				{
					// prepare
					if (!requestInfo.Verb.IsEquals("GET"))
						throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.GetURI()})");

					JObject json = null;
					switch (requestInfo.ObjectName.ToLower())
					{
						case "public":
						case "public-ips":
							var publicAddresses = new JArray();
							Utility.PublicAddresses.ForEach(addr => publicAddresses.Add(new JValue($"{addr}")));
							json = new JObject
							{
								{ "IPs", publicAddresses }
							};
							break;

						case "local":
						case "local-ips":
							var localAddresses = new JArray();
							Utility.LocalAddresses.ForEach(addr => localAddresses.Add(new JValue($"{addr}")));
							json = new JObject
							{
								{ "IPs", localAddresses }
							};
							break;

						case "current":
						case "currentlocation":
							json = (await Utility.GetCurrentLocationAsync(cts.Token, this.Logger, requestInfo.Session.User.ID).ConfigureAwait(false)).ToJson(false, obj => obj.Remove("LastUpdated"));
							break;

						default:
							// prepare
							var ipAddress = requestInfo.GetQueryParameter("ip-address") ?? requestInfo.Session.IP;
							if (string.IsNullOrWhiteSpace(ipAddress))
								throw new InvalidRequestException($"The request is invalid ({requestInfo.Verb} {requestInfo.GetURI()})");

							// same location
							json = (Utility.IsSameLocation(ipAddress)
								? new IPLocation
									{
										ID = ipAddress.GetMD5(),
										IP = ipAddress,
										City = "N/A",
										Region = "N/A",
										Country = "N/A",
										Continent = "N/A",
										Latitude = "N/A",
										Longitude = "N/A",
									}
								: await Utility.GetLocationAsync(ipAddress, cts.Token, this.Logger, requestInfo.Session.User.ID).ConfigureAwait(false)).ToJson(false, obj => obj.Remove("LastUpdated"));
							break;
					}

					// response
					stopwatch.Stop();
					this.WriteLogs(requestInfo, $"Success response - Execution times: {stopwatch.GetElapsedTimes()}");
					if (this.IsDebugResultsEnabled)
						this.WriteLogs(requestInfo,
							$"- Request: {requestInfo.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}" + "\r\n" +
							$"- Response: {json?.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
						);
					return json;
				}
				catch (Exception ex)
				{
					throw this.GetRuntimeException(requestInfo, ex, stopwatch);
				}
		}
	}
}