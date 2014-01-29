﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;

namespace MvcThrottle
{
    public class ThrottlingFilter : ActionFilterAttribute, IActionFilter
    {
        /// <summary>
        /// Creates a new instance of the <see cref="ThrottlingHandler"/> class.
        /// By default, the <see cref="QuotaExceededResponseCode"/> property 
        /// is set to 409 (Conflict).
        /// </summary>
        public ThrottlingFilter()
        {
            QuotaExceededResponseCode = HttpStatusCode.Conflict;
        }

        /// <summary>
        /// Throttling rate limits policy
        /// </summary>
        public ThrottlePolicy Policy { get; set; }

        /// <summary>
        /// Throttle metrics storage
        /// </summary>
        public IThrottleRepository Repository { get; set; }

        /// <summary>
        /// Log traffic and blocked requests
        /// </summary>
        public IThrottleLogger Logger { get; set; }

        /// <summary>
        /// If none specifed the default will be: 
        /// HTTP request quota exceeded! maximum admitted {0} per {1}
        /// </summary>
        public string QuotaExceededMessage { get; set; }

        /// <summary>
        /// Gets or sets the value to return as the HTTP status 
        /// code when a request is rejected because of the
        /// throttling policy. The default value is 409 (Conflict)
        /// </summary>
        public HttpStatusCode QuotaExceededResponseCode { get; set; }

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            var applyThrottling = filterContext.ActionDescriptor.IsDefined(typeof(EnableThrottingAttribute), true) ||
                filterContext.ActionDescriptor.ControllerDescriptor.IsDefined(typeof(EnableThrottingAttribute), true);

            //explicit disabled
            if (filterContext.ActionDescriptor.IsDefined(typeof(DisableThrottingAttribute), true))
            {
                applyThrottling = false;
            }

            if (Policy != null && applyThrottling)
            {
                var identity = SetIndentity(filterContext.HttpContext.Request);
                System.Diagnostics.Debug.WriteLine(identity.ToString());
                if (!IsWhitelisted(identity))
                {
                    TimeSpan timeSpan = TimeSpan.FromSeconds(1);

                    var rates = Policy.Rates.AsEnumerable();
                    if (Policy.StackBlockedRequests)
                    {
                        //all requests including the rejected ones will stack in this order: day, hour, min, sec
                        //if a client hits the hour limit then the minutes and seconds counters will expire and will eventually get erased from cache
                        rates = Policy.Rates.Reverse();
                    }

                    //apply policy
                    //the IP rules are applied last and will overwrite any client rule you might defined
                    foreach (var rate in rates)
                    {
                        var rateLimitPeriod = rate.Key;
                        var rateLimit = rate.Value;

                        switch (rateLimitPeriod)
                        {
                            case RateLimitPeriod.Second:
                                timeSpan = TimeSpan.FromSeconds(1);
                                break;
                            case RateLimitPeriod.Minute:
                                timeSpan = TimeSpan.FromMinutes(1);
                                break;
                            case RateLimitPeriod.Hour:
                                timeSpan = TimeSpan.FromHours(1);
                                break;
                            case RateLimitPeriod.Day:
                                timeSpan = TimeSpan.FromDays(1);
                                break;
                            case RateLimitPeriod.Week:
                                timeSpan = TimeSpan.FromDays(7);
                                break;
                        }

                        //increment counter
                        string requestId;
                        var throttleCounter = ProcessRequest(identity, timeSpan, rateLimitPeriod, out requestId);

                        if (throttleCounter.Timestamp + timeSpan < DateTime.UtcNow)
                            continue;

                        //apply endpoint rate limits
                        if (Policy.EndpointRules != null)
                        {
                            var rules = Policy.EndpointRules.Where(x => identity.Endpoint.Contains(x.Key.ToLowerInvariant())).ToList();
                            if (rules.Any())
                            {
                                //get the lower limit from all applying rules
                                var customRate = (from r in rules let rateValue = r.Value.GetLimit(rateLimitPeriod) select rateValue).Min();

                                if (customRate > 0)
                                {
                                    rateLimit = customRate;
                                }
                            }
                        }

                        //apply custom rate limit for clients that will override endpoint limits
                        if (Policy.ClientRules != null && Policy.ClientRules.Keys.Contains(identity.ClientKey))
                        {
                            var limit = Policy.ClientRules[identity.ClientKey].GetLimit(rateLimitPeriod);
                            if (limit > 0) rateLimit = limit;
                        }

                        //enforce ip rate limit as is most specific 
                        string ipRule = null;
                        if (Policy.IpRules != null && ContainsIp(Policy.IpRules.Keys.ToList(), identity.ClientIp, out ipRule))
                        {
                            var limit = Policy.IpRules[ipRule].GetLimit(rateLimitPeriod);
                            if (limit > 0) rateLimit = limit;
                        }

                        //check if limit is reached
                        if (rateLimit > 0 && throttleCounter.TotalRequests > rateLimit)
                        {
                            //log blocked request
                            if (Logger != null) Logger.Log(ComputeLogEntry(requestId, identity, throttleCounter, rateLimitPeriod.ToString(), rateLimit, filterContext.HttpContext.Request));

                            //break execution and return 409 
                            var message = string.IsNullOrEmpty(QuotaExceededMessage) ?
                                "HTTP request quota exceeded! maximum admitted {0} per {1}" : QuotaExceededMessage;

                            filterContext.Result = QuotaExceededResult(filterContext.RequestContext, string.Format(message, rateLimit, rateLimitPeriod), QuotaExceededResponseCode);
                        }
                    }
                }

            }

            base.OnActionExecuting(filterContext);
        }

        protected virtual RequestIdentity SetIndentity(HttpRequestBase request)
        {
            var entry = new RequestIdentity();
            entry.ClientIp = GetClientIp(request).ToString();
            entry.ClientKey = request.IsAuthenticated ? "auth" : "anon";

            var rd = request.RequestContext.RouteData;
            string currentAction = rd.GetRequiredString("action");
            string currentController = rd.GetRequiredString("controller");

            switch (Policy.EndpointType)
            {
                case EndpointThrottlingType.AbsolutePath:
                    entry.Endpoint = request.Url.AbsolutePath.ToLowerInvariant();
                    break;
                case EndpointThrottlingType.PathAndQuery:
                    entry.Endpoint = request.Url.PathAndQuery.ToLowerInvariant();
                    break;
                case EndpointThrottlingType.ControllerAndAction:
                    entry.Endpoint = currentController + "/" + currentAction;
                    break;
                case EndpointThrottlingType.Controller:
                    entry.Endpoint = request.Url.AbsolutePath.ToLowerInvariant();
                    entry.Endpoint = currentController;
                    break;
                default:
                    break;
            }

            return entry;
        }

        static readonly object _processLocker = new object();
        private ThrottleCounter ProcessRequest(RequestIdentity requestIdentity, TimeSpan timeSpan, RateLimitPeriod period, out string id)
        {
            var throttleCounter = new ThrottleCounter()
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 1
            };

            id = ComputeThrottleKey(requestIdentity, period);

            //serial reads and writes
            lock (_processLocker)
            {
                var entry = Repository.FirstOrDefault(id);
                if (entry.HasValue)
                {
                    //entry has not expired
                    if (entry.Value.Timestamp + timeSpan >= DateTime.UtcNow)
                    {
                        //increment request count
                        var totalRequests = entry.Value.TotalRequests + 1;

                        //deep copy
                        throttleCounter = new ThrottleCounter
                        {
                            Timestamp = entry.Value.Timestamp,
                            TotalRequests = totalRequests
                        };

                    }
                }

                //stores: id (string) - timestamp (datetime) - total (long)
                Repository.Save(id, throttleCounter, timeSpan);
            }

            return throttleCounter;
        }

        protected virtual string ComputeThrottleKey(RequestIdentity requestIdentity, RateLimitPeriod period)
        {
            var keyValues = new List<string>()
                {
                    "throttle"
                };

            if (Policy.IpThrottling)
                keyValues.Add(requestIdentity.ClientIp);

            if (Policy.ClientThrottling)
                keyValues.Add(requestIdentity.ClientKey);

            if (Policy.EndpointThrottling)
                keyValues.Add(requestIdentity.Endpoint);

            keyValues.Add(period.ToString());

            var id = string.Join("_", keyValues);
            var idBytes = Encoding.UTF8.GetBytes(id);
            var hashBytes = new System.Security.Cryptography.SHA1Managed().ComputeHash(idBytes);
            var hex = BitConverter.ToString(hashBytes).Replace("-", "");
            return hex;
        }

        private bool IsWhitelisted(RequestIdentity requestIdentity)
        {
            if (Policy.IpThrottling)
                if (Policy.IpWhitelist != null && ContainsIp(Policy.IpWhitelist, requestIdentity.ClientIp))
                    return true;

            if (Policy.ClientThrottling)
                if (Policy.ClientWhitelist != null && Policy.ClientWhitelist.Contains(requestIdentity.ClientKey))
                    return true;

            if (Policy.EndpointThrottling)
                if (Policy.EndpointWhitelist != null && Policy.EndpointWhitelist.Any(x => requestIdentity.Endpoint.Contains(x.ToLowerInvariant())))
                    return true;

            return false;
        }

        public static string GetClientIp(HttpRequestBase request)
        {
            string ip = null;
            try
            {
                if (request.IsSecureConnection)
                {
                    ip = request.ServerVariables["REMOTE_ADDR"];
                }

                if (string.IsNullOrEmpty(ip))
                {
                    ip = request.ServerVariables["HTTP_X_FORWARDED_FOR"];
                    if (!string.IsNullOrEmpty(ip))
                    {
                        if (ip.IndexOf(",") > 0)
                        {
                            ip = ip.Split(',').Last();
                        }
                    }
                    else
                    {
                        ip = request.UserHostAddress;
                    }
                }
            }
            catch { ip = null; }

            return ip;
        }

        private bool ContainsIp(List<string> ipRules, string clientIp)
        {
            var ip = IPAddress.Parse(clientIp);
            if (ipRules != null && ipRules.Any())
            {
                foreach (var rule in ipRules)
                {
                    var range = new IPAddressRange(rule);
                    if (range.Contains(ip)) return true;
                }
            }

            return false;
        }

        private bool ContainsIp(List<string> ipRules, string clientIp, out string rule)
        {
            rule = null;
            var ip = IPAddress.Parse(clientIp);
            if (ipRules != null && ipRules.Any())
            {
                foreach (var r in ipRules)
                {
                    var range = new IPAddressRange(r);
                    if (range.Contains(ip))
                    {
                        rule = r;
                        return true;
                    }
                }
            }

            return false;
        }

        protected virtual ActionResult QuotaExceededResult(RequestContext context, string message, HttpStatusCode responseCode)
        {
            throw new HttpException(message, 409);
        }

        private ThrottleLogEntry ComputeLogEntry(string requestId, RequestIdentity identity, ThrottleCounter throttleCounter, string rateLimitPeriod, long rateLimit, HttpRequestBase request)
        {
            return new ThrottleLogEntry
            {
                ClientIp = identity.ClientIp,
                ClientKey = identity.ClientKey,
                Endpoint = identity.Endpoint,
                LogDate = DateTime.UtcNow,
                RateLimit = rateLimit,
                RateLimitPeriod = rateLimitPeriod,
                RequestId = requestId,
                StartPeriod = throttleCounter.Timestamp,
                TotalRequests = throttleCounter.TotalRequests,
                Request = request
            };
        }
    }
}
