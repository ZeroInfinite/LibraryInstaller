﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.Web.LibraryInstaller.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Web.LibraryInstaller.Providers.Cdnjs
{
    internal class CdnjsCatalog : ILibraryCatalog
    {
        private const int _days = 3;
        private const string _fileName = "cache.json";
        private const string _remoteApiUrl = "https://aka.ms/g8irvu";
        private const string _metaPackageUrlFormat = "https://api.cdnjs.com/libraries/{0}"; // https://aka.ms/goycwu/{0}

        private readonly string _cacheFile;
        private readonly CdnjsProvider _provider;
        private IEnumerable<CdnjsLibraryGroup> _libraryGroups;

        public CdnjsCatalog(CdnjsProvider provider)
        {
            _provider = provider;
            _cacheFile = Path.Combine(provider.CacheFolder, _fileName);
        }

        public async Task<CompletionSet> GetLibraryCompletionSetAsync(string value, int caretPosition)
        {
            if (!await EnsureCatalogAsync(CancellationToken.None).ConfigureAwait(false))
            {
                return default(CompletionSet);
            }

            var span = new CompletionSet
            {
                Start = 0,
                Length = value.Length
            };

            int at = value.IndexOf('@');
            string name = at > -1 ? value.Substring(0, at) : value;

            var completions = new List<CompletionItem>();

            // Name
            if (at == -1 || caretPosition <= at)
            {
                IReadOnlyList<ILibraryGroup> result = await SearchAsync(name, int.MaxValue, CancellationToken.None).ConfigureAwait(false);

                foreach (CdnjsLibraryGroup group in result)
                {
                    var completion = new CompletionItem
                    {
                        DisplayText = AliasedName(group.DisplayName),
                        InsertionText = group.DisplayName + "@" + group.Version,
                        Description = group.Description,
                    };

                    completions.Add(completion);
                }
            }

            // Version
            else
            {
                CdnjsLibraryGroup group = _libraryGroups.FirstOrDefault(g => g.DisplayName == name);

                if (group != null)
                {
                    span.Start = at + 1;
                    span.Length = value.Length - span.Start;

                    IEnumerable<Asset> assets = await GetAssetsAsync(name, CancellationToken.None).ConfigureAwait(false);

                    foreach (string version in assets.Select(a => a.Version))
                    {
                        var completion = new CompletionItem
                        {
                            DisplayText = version,
                            InsertionText = $"{name}@{version}",
                        };

                        completions.Add(completion);
                    }
                }
            }

            span.Completions = completions;

            return span;
        }

        public async Task<IReadOnlyList<ILibraryGroup>> SearchAsync(string term, int maxHits, CancellationToken cancellationToken)
        {
            if (!await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false))
            {
                return Enumerable.Empty<ILibraryGroup>().ToList();
            }

            IEnumerable<CdnjsLibraryGroup> results;

            if (string.IsNullOrEmpty(term))
            {
                results = _libraryGroups.Take(maxHits);
            }
            else
            {
                results = GetSortedSearchResult(term).Take(maxHits);
            }

            foreach (CdnjsLibraryGroup group in results)
            {
                string groupName = group.DisplayName;
                group.DisplayInfosTask = ct => GetLibraryIdsAsync(groupName, ct);
            }

            return results.ToList();
        }

        public async Task<ILibrary> GetLibraryAsync(string libraryId, CancellationToken cancellationToken)
        {
            try
            {
                string[] args = libraryId.Split('@');
                string name = args[0];
                string version = args[1];

                IEnumerable<Asset> assets = await GetAssetsAsync(name, cancellationToken).ConfigureAwait(false);
                Asset asset = assets.FirstOrDefault(a => a.Version == version);

                if (asset == null)
                {
                    throw new InvalidLibraryException(libraryId, _provider.Id);
                }

                return new CdnjsLibrary
                {
                    Version = asset.Version,
                    Files = asset.Files.ToDictionary(k => k, b => b == asset.DefaultFile),
                    Name = name,
                    ProviderId = _provider.Id,
                };
            }
            catch (Exception)
            {
                throw new InvalidLibraryException(libraryId, _provider.Id);
            }
        }

        public async Task<string> GetLatestVersion(string libraryId, bool includePreReleases, CancellationToken cancellationToken)
        {
            string[] args = libraryId.Split('@');

            if (args.Length < 2)
            {
                return null;
            }

            if (!await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            string name = args[0];

            CdnjsLibraryGroup group = _libraryGroups.FirstOrDefault(l => l.DisplayName == name);

            if (group == null)
            {
                return null;
            }

            var ids = (await GetLibraryIdsAsync(group.DisplayName, cancellationToken).ConfigureAwait(false)).ToList();
            string first = ids[0];

            if (!includePreReleases)
            {
                first = ids.First(id => id.Substring(name.Length).Any(c => !char.IsLetter(c)));
            }

            if (!string.IsNullOrEmpty(first) && ids.IndexOf(first) < ids.IndexOf(libraryId))
            {
                return first;
            }

            return null;
        }

        private IEnumerable<CdnjsLibraryGroup> GetSortedSearchResult(string term)
        {
            var list = new List<Tuple<int, CdnjsLibraryGroup>>();

            foreach (CdnjsLibraryGroup group in _libraryGroups)
            {
                string cleanName = NormalizedGroupName(group.DisplayName);

                if (cleanName.Equals(term, StringComparison.OrdinalIgnoreCase))
                    list.Add(Tuple.Create(50, group));
                else if (group.DisplayName.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                    list.Add(Tuple.Create(20 + (term.Length - cleanName.Length), group));
                else if (group.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) > -1)
                    list.Add(Tuple.Create(1, group));
            }

            return list.OrderByDescending(t => t.Item1).Select(t => t.Item2);
        }

        private static string NormalizedGroupName(string groupName)
        {
            string cleanName = AliasedName(groupName);

            if (cleanName.EndsWith("js"))
            {
                cleanName = cleanName
                    .Substring(0, cleanName.Length - 2)
                    .TrimEnd('-', '.');
            }

            return cleanName;
        }

        private static string AliasedName(string groupName)
        {

            switch (groupName)
            {
                case "twitter-bootstrap":
                    return "bootstrap";
            }

            return groupName;
        }

        private async Task<bool> EnsureCatalogAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                string json = await FileHelpers.GetFileTextAsync(_remoteApiUrl, _cacheFile, _days, cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(json))
                {
                    return false;
                }

                string obj = ((JObject)JsonConvert.DeserializeObject(json))["results"].ToString();

                _libraryGroups = JsonConvert.DeserializeObject<IEnumerable<CdnjsLibraryGroup>>(obj).ToArray();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Write(ex);
                return false;
            }
        }

        private async Task<IEnumerable<string>> GetLibraryIdsAsync(string groupName, CancellationToken cancellationToken)
        {
            IEnumerable<Asset> assets = await GetAssetsAsync(groupName, cancellationToken).ConfigureAwait(false);

            return assets?.Select(a => $"{groupName}@{a.Version}");
        }

        private async Task<IEnumerable<Asset>> GetAssetsAsync(string groupName, CancellationToken cancellationToken)
        {
            string localFile = Path.Combine(_provider.CacheFolder, groupName, "metadata.json");
            var list = new List<Asset>();

            try
            {
                string url = string.Format(_metaPackageUrlFormat, groupName);
                string json = await FileHelpers.GetFileTextAsync(url, localFile, _days, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(json))
                {
                    var root = JObject.Parse(json);
                    IEnumerable<Asset> assets = JsonConvert.DeserializeObject<IEnumerable<Asset>>(root["assets"].ToString());
                    string defaultFileName = root["filename"]?.Value<string>();

                    foreach (Asset asset in assets)
                    {
                        asset.DefaultFile = defaultFileName;
                        list.Add(asset);
                    }
                }
            }
            catch (Exception)
            {
                throw new InvalidLibraryException(groupName, _provider.Id);
            }

            return list;
        }
    }

    internal class Asset
    {
        public string Version { get; set; }
        public string[] Files { get; set; }
        public string DefaultFile { get; set; }
    }
}