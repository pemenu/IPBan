﻿/*
MIT License

Copyright (c) 2019 Digital Ruby, LLC - https://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DigitalRuby.IPBanCore
{
    /// <summary>
    /// Create a block firewall rule from ip addresses from a uri
    /// </summary>
    public class IPBanUriFirewallRule : IUpdater
    {
        private readonly IIPBanFirewall firewall;
        private readonly IIsWhitelisted whitelistChecker;
        private readonly HttpClient httpClient;

        private DateTime lastRun;

        /// <summary>
        /// Rule prefix
        /// </summary>
        public string RulePrefix { get; }

        /// <summary>
        /// Interval
        /// </summary>
        public TimeSpan Interval { get; }

        /// <summary>
        /// Uri
        /// </summary>
        public Uri Uri { get; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="firewall">The firewall to block with</param>
        /// <param name="whitelistChecker">Whitelist checker</param>
        /// <param name="rulePrefix">Firewall rule prefix</param>
        /// <param name="interval">Interval to check uri for changes</param>
        /// <param name="uri">Uri, can be either file or http(s).</param>
        public IPBanUriFirewallRule(IIPBanFirewall firewall, IIsWhitelisted whitelistChecker, string rulePrefix, TimeSpan interval, Uri uri)
        {
            this.firewall = firewall;
            this.whitelistChecker = whitelistChecker;
            RulePrefix = rulePrefix;
            Uri = uri;
            Interval = interval;

            if (!uri.IsFile)
            {
                // ensure uri ends with slash
                if (!uri.ToString().EndsWith("/"))
                {
                    uri = new Uri(uri.ToString() + "/");
                }
                httpClient = new HttpClient { BaseAddress = uri };
            }
        }

        /// <summary>
        /// Cleanup all resources
        /// </summary>
        public void Dispose()
        {
            httpClient?.Dispose();
        }

        /// <summary>
        /// Convert to a string
        /// </summary>
        /// <returns>String</returns>
        public override string ToString()
        {
            return $"Prefix: {RulePrefix}, Interval: {Interval}, Uri: {Uri}";
        }

        /// <summary>
        /// Get hash code for this uri firewall rule
        /// </summary>
        /// <returns>Hash code</returns>
        public override int GetHashCode()
        {
            return Uri.GetHashCode();
        }

        /// <summary>
        /// Check if equal to another object
        /// </summary>
        /// <param name="obj">Other object</param>
        /// <returns>True if equal, false otherwise</returns>
        public override bool Equals(object obj)
        {
            if (!(obj is IPBanUriFirewallRule rule))
            {
                return false;
            }
            return (RulePrefix.Equals(rule.RulePrefix) && Uri.Equals(rule.Uri) && Interval.Equals(rule.Interval));
        }

        /// <summary>
        /// Update the updater
        /// </summary>
        /// <param name="cancelToken">Cancel token</param>
        /// <returns>Task</returns>
        public async Task Update(CancellationToken cancelToken)
        {
            DateTime now = IPBanService.UtcNow;
            if ((now - lastRun) >= Interval)
            {
                lastRun = now;
                if (Uri.IsFile)
                {
                    await ProcessResult(await File.ReadAllTextAsync(Uri.LocalPath, cancelToken), cancelToken);
                }
                else
                {
                    HttpResponseMessage response = await httpClient.GetAsync(string.Empty, cancelToken);
                    response.EnsureSuccessStatusCode();
                    string text = await response.Content.ReadAsStringAsync();
                    await ProcessResult(text, cancelToken);
                }
            }
        }

        /// <summary>
        /// Delete firewall rules that may have been created by this rule
        /// </summary>
        public void DeleteRule()
        {
            foreach (string ruleName in firewall.GetRuleNames(RulePrefix).ToArray())
            {
                firewall.DeleteRule(ruleName);
            }
        }

        private Task ProcessResult(string text, CancellationToken cancelToken)
        {
            using StringReader reader = new StringReader(text);
            string line;
            List<IPAddressRange> ranges = new List<IPAddressRange>();
            int lines = 0;

            while ((line = reader.ReadLine()) != null)
            {
                if (lines++ > 10000)
                {
                    // prevent too many lines from crashing us
                    break;
                }

                // trim line, ignore if necessary
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("'") || line.StartsWith("REM") ||
                    !IPAddressRange.TryParse(line, out IPAddressRange range))
                {
                    continue;
                }
                else if (whitelistChecker is null || !whitelistChecker.IsWhitelisted(range))
                {
                    // make sure to add only ranges that are not whitelisted
                    ranges.Add(range);
                }
            }

            return firewall.BlockIPAddresses(RulePrefix, ranges, null, cancelToken);
        }
    }
}
