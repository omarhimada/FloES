﻿using Amazon.Extensions.NETCore.Setup;
using Elasticsearch.Net;
using Elasticsearch.Net.Aws;
using Microsoft.Extensions.Logging;
using Nest;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FloES
{
    /// <summary>
    /// Wrapper for a Nest ElasticClient with common Elasticsearch operations
    /// - can use with 'await using', and includes ILogger support
    /// </summary>
    public class Floe<T> : IAsyncDisposable, IDisposable
    {
        private bool _disposed;

        /// <summary>
        /// Optional logger
        /// </summary>
        private readonly ILogger<T> _logger;

        /// <summary>
        /// The single instance of ElasticClient that lives as long as this Floe object does
        /// </summary>
        private readonly ElasticClient _client;

        /// <summary>
        /// Collection of documents to write to an Elasticsearch index
        /// </summary>
        private readonly List<object> _documents = new List<object>();

        /// <summary>
        /// Default index to read/write the documents 
        /// </summary>
        private readonly string _defaultIndex;

        /// <summary>
        /// Whether or not to use a rolling date pattern for the indices
        /// </summary>
        private readonly bool _rollingDate;

        /// <summary>
        /// Default number of documents to write to Elasticsearch in each BulkRequest
        /// (5 is a safe number)
        /// </summary>
        // ReSharper disable once InconsistentNaming
        private const int _defaultNumberOfBulkDocumentsToWriteAtOnce = 5;

        /// <summary>
        /// Number of documents to write to Elasticsearch in each BulkRequest
        /// </summary>
        private readonly int _numberOfBulkDocumentsToWriteAtOnce;

        /// <summary>
        /// Build the 'Index' string for the BulkRequests
        /// </summary>
        private string IndexToWriteTo(string index = null)
        {
            string prefix = string.IsNullOrEmpty(index) ? _defaultIndex : index;
            string suffix = _rollingDate ? $"-{DateTime.UtcNow:yyyy.MM.dd}" : string.Empty;
            return $"{prefix}{suffix}";
        }

        /// <summary>
        /// Build the 'Index' string for the scrolls and searches
        /// </summary>
        private string IndexToSearch(string index = null) => string.IsNullOrEmpty(index) ? $"{_defaultIndex}*" : $"{index}*";

        #region Constructors
        /// <summary>
        /// Use this constructor if the ElasticClient has already been instantiated
        /// </summary>
        /// <param name="client"></param>
        /// <param name="defaultIndex">(Optional) default index to use for writing documents</param>
        /// <param name="numberOfBulkDocumentsToWriteAtOnce">
        ///     (Optional) number of documents to write to Elasticsearch
        ///     - set to 0 to write every record immediately, default is 5
        /// </param>
        /// <param name="rollingDate">(Optional) whether or not to use a rolling date pattern when writing to indices, default is false</param>
        /// <param name="logger">(Optional) ILogger to use</param>
        public Floe(
            ElasticClient client,
            string defaultIndex = null,
            int numberOfBulkDocumentsToWriteAtOnce = _defaultNumberOfBulkDocumentsToWriteAtOnce,
            bool rollingDate = false,
            ILogger<T> logger = null)
        {
            if (!string.IsNullOrEmpty(defaultIndex))
            {
                _defaultIndex = defaultIndex;
            }

            _rollingDate = rollingDate;

            if (numberOfBulkDocumentsToWriteAtOnce > -1)
            {
                _numberOfBulkDocumentsToWriteAtOnce = numberOfBulkDocumentsToWriteAtOnce;
            }

            _client = client;

            _logger = logger;
        }

        /// <summary>
        /// This constructor instantiates a new ElasticClient using the AWSOptions and AWS cluster URI
        /// </summary>
        /// <param name="awsOptions">AWSOptions containing the credentials and region endpoint</param>
        /// <param name="esClusterUri">URI of the Elasticsearch cluster in AWS</param>
        /// <param name="defaultIndex">(Optional) default index to use for writing documents</param>
        /// <param name="numberOfBulkDocumentsToWriteAtOnce">
        ///     (Optional) number of documents to write to Elasticsearch
        ///     - set to 0 to write every record immediately, default is 5
        /// </param>
        /// <param name="rollingDate">(Optional) whether or not to use a rolling date pattern when writing to indices, default is false</param>
        /// <param name="logger">(Optional) ILogger to use</param>
        public Floe(
            AWSOptions awsOptions,
            Uri esClusterUri,
            string defaultIndex = null,
            int numberOfBulkDocumentsToWriteAtOnce = _defaultNumberOfBulkDocumentsToWriteAtOnce,
            bool rollingDate = false,
            ILogger<T> logger = null)
        {
            if (awsOptions == null)
            {
                throw new ArgumentNullException(nameof(awsOptions));
            }

            if (!string.IsNullOrEmpty(defaultIndex))
            {
                _defaultIndex = defaultIndex;
            }

            _rollingDate = rollingDate;

            if (numberOfBulkDocumentsToWriteAtOnce > -1)
            {
                _numberOfBulkDocumentsToWriteAtOnce = numberOfBulkDocumentsToWriteAtOnce;
            }

            AwsHttpConnection httpConnection = new AwsHttpConnection(awsOptions);

            SingleNodeConnectionPool connectionPool = new SingleNodeConnectionPool(esClusterUri);
            ConnectionSettings connectionSettings = new ConnectionSettings(connectionPool, httpConnection);

            _client = new ElasticClient(connectionSettings);

            _logger = logger;
        }
        #endregion

        /// <summary>
        /// List all documents in an index asynchronously using the scroll API
        /// </summary>
        /// <typeparam name="T">Index POCO</typeparam>
        /// <param name="listLast24Hours">(Optional) whether or not to list using the last 24 hours of the UTC date - default is false</param>
        /// <param name="listLast7Days">(Optional) whether or not to list using the last 7 days of the UTC date - default is false</param>
        /// <param name="listLast31Days">(Optional) whether or not to list using the last 31 days of the UTC date - default is false</param>
        /// <param name="scrollTime">(Optional) TTL of the scroll until another List is called - default is 60s</param>
        /// <param name="index">(Optional) index to scroll - if none provided the default index will be used</param>
        public async Task<IEnumerable<T>> List<T>(
          bool listLast24Hours = false,
          bool listLast7Days = false,
          bool listLast31Days = false,
          string scrollTime = "60s",
          string index = null) where T : class
        {
            string indexToScroll = IndexToSearch(index);
            ISearchResponse<T> searchResponse = null;
            List<T> results = new List<T>();

            if (!listLast24Hours && !listLast7Days && !listLast31Days)
            {
                searchResponse =
                  await _client.SearchAsync<T>(sd => sd
                    .Index(indexToScroll)
                    .From(0)
                    .Take(1000)
                    .MatchAll()
                    .Scroll(scrollTime));
            }
            else if (listLast24Hours)
            {
                // Scroll for the last day only (UTC)
                searchResponse =
                  await _client.SearchAsync<T>(sd => sd
                    .Index(indexToScroll)
                    .From(0)
                    .Take(1000)
                    .Query(query =>
                      query.DateRange(s => s
                        .Field("timeStamp")
                        .GreaterThanOrEquals(
                            DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)))))
                    .Scroll(scrollTime));
            }
            else if (listLast7Days)
            {
                // Scroll for the last week only (UTC)
                searchResponse =
                  await _client.SearchAsync<T>(sd => sd
                    .Index(indexToScroll)
                    .From(0)
                    .Take(1000)
                    .Query(query =>
                      query.DateRange(s => s
                        .Field("timeStamp")
                        .GreaterThanOrEquals(
                            DateTime.UtcNow.Subtract(TimeSpan.FromDays(7)))))
                    .Scroll(scrollTime));
            }
            else if (listLast31Days)
            {
                // Scroll for the last month only (UTC)
                searchResponse =
                  await _client.SearchAsync<T>(sd => sd
                    .Index(indexToScroll)
                    .From(0)
                    .Take(1000)
                    .Query(query =>
                      query.DateRange(s => s
                        .Field("timeStamp")
                        .GreaterThanOrEquals(
                            DateTime.UtcNow.Subtract(TimeSpan.FromDays(31)))))
                    .Scroll(scrollTime));
            }
            else if (listLast24Hours && listLast7Days && listLast31Days)
            {
                _logger?.LogInformation($"~ ~ ~ Floe was told to list both the last 24 hours, 7 days and 31 days simultaneously, in its confusion it decided to return nothing");
                return results;
            }

            if (searchResponse == null || string.IsNullOrEmpty(searchResponse.ScrollId))
            {
                _logger?.LogInformation($"~ ~ ~ Floe received a null search response or failed to scroll (index may not exist)");
                return results;
            }

            bool continueScrolling = true;
            while (continueScrolling && searchResponse != null)
            {
                if (searchResponse.Documents != null && !searchResponse.IsValid)
                {
                    _logger?.LogError($"~ ~ ~ Floe received an error while listing (scrolling) {searchResponse.ServerError?.Error?.Reason}");
                    break;
                }

                if (searchResponse.Documents != null && !searchResponse.Documents.Any())
                {
                    continueScrolling = false;
                }
                else
                {
                    results.AddRange(searchResponse.Documents);
                    searchResponse = await _client.ScrollAsync<T>(scrollTime, searchResponse.ScrollId);
                }
            }

            await _client.ClearScrollAsync(new ClearScrollRequest(searchResponse.ScrollId));

            return results;
        }

        /// <summary>
        /// Search for documents
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="fieldToSearch">The name of the field in the document to search (e.g.: "customerId" or "animal.name")</param>
        /// <param name="valueToSearch">The value to search for</param>
        /// <param name="searchToday">(Optional) whether or not to limit the search to the last 24 hours of the current UTC date - default is false</param>
        /// <param name="index">(Optional) index to search - if none provided the default index will be used</param>
        public async Task<IEnumerable<T>> Search<T>(
          string fieldToSearch,
          object valueToSearch,
          bool searchToday = false,
          string index = null) where T : class
        {
            string indexToSearch = IndexToSearch(index);

            ISearchResponse<T> searchResponse;
            if (!searchToday)
            {
                searchResponse =
                  await _client.SearchAsync<T>(s => s
                    .Size(10000)
                    .Index(indexToSearch)
                    .Query(q =>
                      q.Match(c => c
                        .Field(fieldToSearch)
                        .Query(valueToSearch.ToString()))));
            }
            else
            {
                searchResponse =
                  await _client.SearchAsync<T>(sd => sd
                    .Index(indexToSearch)
                    .Query(query =>
                      query.DateRange(s => s
                        .Field("timeStamp")
                        .GreaterThanOrEquals(
                          DateTime.UtcNow.Subtract(TimeSpan.FromDays(1))))));
            }

            if (searchResponse?.Documents != null && !searchResponse.IsValid)
            {
                return searchResponse.Documents;
            }

            _logger?.LogError($"~ ~ ~ Floe received an error while searching for [{fieldToSearch},{valueToSearch}]: {searchResponse?.ServerError?.Error?.Reason}");

            return null;
        }

        /// <summary>
        /// Count all the documents in an index
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="index">(Optional) index to count - if none provided the default index will be used</param>
        public async Task<long> Count<T>(
          string index = null) where T : class
        {
            string indexToCount = IndexToSearch(index);

            try
            {
                CountResponse countResponse = 
                  await _client.CountAsync<T>(c => c
                    .Index(indexToCount));

                return countResponse.Count;
            }
            catch (Exception exception)
            {
                string exceptionLogPrefix = $"~ ~ ~ Floe threw an exception while trying to count documents in index {indexToCount}";

                string errorMessage =
                  $"{exceptionLogPrefix}{Environment.NewLine}{JsonConvert.SerializeObject(exception)}";

                _logger?.LogError(errorMessage);

                // ReSharper disable once PossibleIntendedRethrow
                throw exception;
            }
        }

        /// <summary>
        /// Count all the documents in an index that match a given search query
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="fieldToSearch">The name of the field in the document to search (e.g.: "customerId" or "animal.name")</param>
        /// <param name="valueToSearch">The value to search for</param>
        /// <param name="index">(Optional) index to count - if none provided the default index will be used</param>
        public async Task<long> CountBySearch<T>(
          string fieldToSearch,
          object valueToSearch,
          string index = null) where T : class
        {
            string indexToCount = IndexToSearch(index);

            try
            {
                CountResponse countResponse =
                  await _client.CountAsync<T>(c => c
                    .Index(indexToCount)
                    .Query(q => q
                      .Match(m => m
                        .Field(fieldToSearch)
                        .Query(valueToSearch.ToString()))));

                return countResponse.Count;
            }
            catch (Exception exception)
            {
                string exceptionLogPrefix = $"~ ~ ~ Floe threw an exception while trying to count documents in index {indexToCount}";

                string errorMessage =
                  $"{exceptionLogPrefix}{Environment.NewLine}{JsonConvert.SerializeObject(exception)}";

                _logger?.LogError(errorMessage);

                // ReSharper disable once PossibleIntendedRethrow
                throw exception;
            }
        }

        /// <summary>
        /// Write the document to an Elasticsearch index. Uses BulkAsync and 'numberOfBulkDocumentsToWriteAtOnce'
        /// </summary>
        /// <typeparam name="T">Index POCO</typeparam>
        /// <param name="document">Document to write to the Elasticsearch index</param>
        /// <param name="allowDuplicates">(Optional) if true the documents about to be bulk written will not be validated for duplicates (side-effect of async operations) - default is false</param>
        /// <param name="index">(Optional) index to write to - if none provided the default index will be used</param>
        public async Task Write<T>(
          T document,
          bool allowDuplicates = false,
          string index = null)
        {
            _documents.Add(document);

            string indexToWriteTo = IndexToWriteTo(index);

            try
            {
                // Ensure we are only making requests when we have enough documents
                if (_documents.Count >= _numberOfBulkDocumentsToWriteAtOnce)
                {
                    BulkDescriptor bulkDescriptor = new BulkDescriptor();
                    bulkDescriptor
                      .IndexMany(!allowDuplicates ? _documents.Distinct() : _documents)
                      .Index(indexToWriteTo);

                    BulkResponse bulkResponse = await _client.BulkAsync(bulkDescriptor);

                    if (bulkResponse.Errors)
                    {
                        string errorLogPrefix = $"~ ~ ~ Floe received an error while trying to write to index {indexToWriteTo}";

                        string errorMessage =
                          $"{errorLogPrefix}{Environment.NewLine}{JsonConvert.SerializeObject(bulkResponse.Errors)}";

                        _logger?.LogError(errorMessage);

                        throw new Exception(errorMessage);
                    }

                    _documents.Clear();
                }
            }
            catch (Exception exception)
            {
                string exceptionLogPrefix = $"~ ~ ~ Floe threw an exception while trying to write to index {indexToWriteTo}";

                string errorMessage =
                  $"{exceptionLogPrefix}{Environment.NewLine}{JsonConvert.SerializeObject(exception)}";

                _logger?.LogError(errorMessage);

                // ReSharper disable once PossibleIntendedRethrow
                throw exception;
            }
        }

        /// <summary>
        /// Write any remaining documents queue. If calling Write many times, you may want to call this method once at the end to 'flush'
        /// any unwritten documents from the queue (e.g.: if your numberOfBulkDocumentsToWriteAtOnce is 5, and you last wrote 4 documents, calling this method
        /// will write the final document)
        /// </summary>
        /// <typeparam name="T">Index POCO</typeparam>
        /// <param name="index">(Optional) index to write to - if none provided the default index will be used</param>
        public async Task WriteUnwritten<T>(
          bool allowDuplicates = false,
          string index = null)
        {
            string indexToWriteTo = IndexToWriteTo(index);

            try
            {
                BulkDescriptor bulkDescriptor = new BulkDescriptor();
                bulkDescriptor
                  .IndexMany(!allowDuplicates ? _documents.Distinct() : _documents)
                  .Index(indexToWriteTo);

                BulkResponse bulkResponse = await _client.BulkAsync(bulkDescriptor);

                if (bulkResponse.Errors)
                {
                    string errorLogPrefix = $"~ ~ ~ Floe received an error while trying to write all unwritten documents to index {indexToWriteTo}";

                    string errorMessage =
                      $"{errorLogPrefix}{Environment.NewLine}{JsonConvert.SerializeObject(bulkResponse.Errors)}";

                    _logger?.LogError(errorMessage);

                    throw new Exception(errorMessage);
                }

                _documents.Clear();
            }
            catch (Exception exception)
            {
                string exceptionLogPrefix = $"~ ~ ~ Floe threw an exception while trying to write all unwritten documents to index {indexToWriteTo}";

                string errorMessage =
                  $"{exceptionLogPrefix}{Environment.NewLine}{JsonConvert.SerializeObject(exception)}";

                _logger?.LogError(errorMessage);

                // ReSharper disable once PossibleIntendedRethrow
                throw exception;
            }
        }

        /// <summary>
        /// Find a document by its ID
        /// </summary>
        /// <typeparam name="T">Index POCO</typeparam>
        /// <param name="id">ID of the document</param>
        /// <returns>Null if no document was found</returns>
        public async Task<T> Find<T>(string id) where T : class
        {
            GetResponse<T> response = await _client.GetAsync<T>(id);

            if (response.Found)
            {
                _logger?.LogInformation($"~ ~ ~ Floe found document of type {typeof(T).Name} with ID {id}");

                return response.Source;
            }

            _logger?.LogInformation($"~ ~ ~ Floe could not find document of type {typeof(T).Name} with ID {id}");

            return null;
        }

        /// <summary>
        /// Delete all indices (does not delete system indices)
        /// </summary>
        /// <returns>True if all indices were successfully deleted</returns>
        public async Task<bool> DeleteAllIndices()
        {
            _logger?.LogInformation($"~ ~ ~ Floe is deleting all indices");

            List<bool> indexDeletions = new List<bool>();
            foreach (KeyValuePair<IndexName, IndexState> index
            in (await _client.Indices.GetAsync(new GetIndexRequest(Indices.All))).Indices)
            {
                // Do not delete system indices
                if (!index.Key.Name.StartsWith('.'))
                {
                    indexDeletions.Add(await DeleteIndex(index.Key.Name));
                }
            }

            return indexDeletions.All(indexDeletion => true);
        }

        /// <summary>
        /// Delete an index (does not delete system indices)
        /// </summary>
        /// <param name="index">Index name</param>
        public async Task<bool> DeleteIndex(string index)
        {
            // Do not delete system indices
            if (!string.IsNullOrEmpty(index) && !index.StartsWith('.'))
            {
                _logger?.LogInformation($"~ ~ ~ Floe is deleting index {index}");

                return (await _client.Indices.DeleteAsync(index)).IsValid;
            }

            return false;
        }

        #region Disposable implementation
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        public virtual ValueTask DisposeAsync()
        {
            try
            {
                Dispose();
                return default;
            }
            catch (Exception exception)
            {
                return new ValueTask(Task.FromException(exception));
            }
        }
        #endregion
    }
}
