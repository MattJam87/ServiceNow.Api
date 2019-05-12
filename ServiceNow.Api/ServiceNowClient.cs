﻿
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServiceNow.Api.Exceptions;
using ServiceNow.Api.MetaData;
using ServiceNow.Api.Tables;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace ServiceNow.Api
{
	public class ServiceNowClient : IDisposable
	{
		private const int DefaultPageSize = 1000;
		private readonly ILogger _logger;
		private readonly HttpClient _httpClient;
		private readonly Options _options;

		public ServiceNowClient(
			 string account,
			 string username,
			 string password,
			Options options = null)
		{
			_options = options;

			// Accept the ILogger passed in on options or create a NullLogger
			_logger = options.Logger ?? new NullLogger<ServiceNowClient>();

			AccountName = account;
			if (account == null)
			{
				throw new ArgumentNullException(nameof(account));
			}

			if (username == null)
			{
				throw new ArgumentNullException(nameof(username));
			}

			if (password == null)
			{
				throw new ArgumentNullException(nameof(password));
			}

			var httpClientHandler = new HttpClientHandler();
			_httpClient = new HttpClient(httpClientHandler)
			{
				BaseAddress = new Uri($"https://{account}.service-now.com"),
				DefaultRequestHeaders =
				{
					Accept = {new MediaTypeWithQualityHeaderValue("application/json")},
				}
			};
			_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}")));
			_logger.LogDebug("Created ServiceNowClient instance.");
		}

		public ServiceNowClient(
			 string account,
			 string username,
			 string password,
			ILogger iLogger = null)
		: this(account, username, password, new Options { Logger = iLogger })
		{
		}

		public string AccountName { get; }

		public void Dispose() => _httpClient?.Dispose();

		[Obsolete("Use GetAllByQueryAsync instead.")]
		public Task<List<T>> GetAllByQuery<T>(string query = null, CancellationToken cancellationToken = default) where T : Table
			=> GetAllByQueryAsync<T>(query, cancellationToken);

		public Task<List<T>> GetAllByQueryAsync<T>(string query = null, CancellationToken cancellationToken = default) where T : Table
		{
			_logger.LogDebug($"Calling {nameof(GetAllByQuery)}" +
							 $" type: {typeof(T)}" +
							 $", {nameof(query)}:{query ?? "<not set>"}" +
							 ".");
			return GetAllByQueryInternalAsync<T>(Table.GetTableName<T>(), query, null, null, DefaultPageSize, cancellationToken);
		}

		[Obsolete("Use GetAllByQueryAsync instead.")]
		public Task<List<JObject>> GetAllByQuery(string tableName, string query = null, List<string> fieldList = null, string extraQueryString = null, CancellationToken cancellationToken = default)
			=> GetAllByQueryAsync(tableName, query, fieldList, extraQueryString, cancellationToken);

		public Task<List<JObject>> GetAllByQueryAsync(string tableName, string query = null, List<string> fieldList = null, string extraQueryString = null, CancellationToken cancellationToken = default)
		{
			_logger.LogDebug($"Calling {nameof(GetAllByQuery)}" +
							 $" {nameof(tableName)}: {tableName}" +
							 $", {nameof(query)}: {query ?? "<not set>"}" +
							 $", {nameof(fieldList)}: {(fieldList?.Any() == true ? string.Join(", ", fieldList) : "<not set>")}" +
							 ".");
			return GetAllByQueryInternalAsync<JObject>(tableName, query, fieldList, extraQueryString, DefaultPageSize, cancellationToken);
		}

		internal async Task<List<T>> GetAllByQueryInternalAsync<T>(string tableName, string query, List<string> fieldList, string extraQueryString, int take, CancellationToken cancellationToken)
		{
			_logger.LogTrace($"Entered {nameof(GetAllByQueryInternalAsync)}" +
							 $" type: {typeof(T)}" +
							 $", {nameof(tableName)}: {tableName}" +
							 $", {nameof(query)}: {query ?? "<not set>"}" +
							 $", {nameof(fieldList)}: {(fieldList?.Any() == true ? string.Join(", ", fieldList) : "<not set>")}" +
							 $", {nameof(extraQueryString)}: {(string.IsNullOrWhiteSpace(extraQueryString) ? "<not set>" : extraQueryString)}" +
							 ".");
			// To avoid issues with duplicates we should sort by something.
			// Does the query contain an ORDERBY?
			if (query?.Contains("ORDERBY") != true)
			{
				// NO - So set a default
				if (query == null)
				{
					query = "ORDERBYsys_created_on";
				}
				else
				{
					query += "^ORDERBYsys_created_on";
				}
			}

			var skip = 0;
			var finished = false;
			// Prepare our final response
			var finalResult = new Page<T>();
			// While the last request returned at least the pageSize then we continue
			// TODO - FIX PAGING! This should take the maximum item in the ORDERBY and use that as a ">=", eliminating any duplicates in the paging output
			while (!finished)
			{
				// Skip the number of "take" entries each time
				var response = await GetPageByQueryInternalAsync<T>(skip, take, tableName, query, fieldList, extraQueryString, cancellationToken).ConfigureAwait(false);
				// Get the next page next time round
				skip += take;
				// Add this response to the list
				finalResult.Items.AddRange(response.Items);
				// If we got at least the number we asked for then there are probably more
				if (response.Items.Count == take)
				{
					continue;
				}

				// All done
				finished = true;

				// If required, double check how many we got is how many we should have
				finalResult.TotalCount = response.TotalCount;
				if (_options.ValidateCountItemsReturned && finalResult.TotalCount != finalResult.Items.Count)
				{
					throw new Exception($"Expected {finalResult.TotalCount} {typeof(T)} but only retrieved {finalResult.Items.Count}");
				}
			}

			// https://community.servicenow.com/community?id=community_question&sys_id=bd7f8725dbdcdbc01dcaf3231f961949
			// See if we have any dupes based on sys_id
			if (finalResult.Items.Count > 0)
			{
				// Do we have a sys_id to work with

			}

			return finalResult.Items;
		}

		[Obsolete("Use GetPageByQueryAsync instead.")]
		public Task<Page<JObject>> GetPageByQuery(int skip, int take, string tableName, string query = null, List<string> fieldList = null, string extraQueryString = null, CancellationToken cancellationToken = default)
			=> GetPageByQueryAsync(skip, take, tableName, query, fieldList, extraQueryString, cancellationToken);

		public Task<Page<JObject>> GetPageByQueryAsync(int skip, int take, string tableName, string query = null, List<string> fieldList = null, string extraQueryString = null, CancellationToken cancellationToken = default)
			=> GetPageByQueryInternalAsync<JObject>(skip, take, tableName, query, fieldList, extraQueryString, cancellationToken);

		[Obsolete("Use GetPageByQueryAsync instead.")]
		public Task<Page<T>> GetPageByQuery<T>(int skip, int take, string query = null, CancellationToken cancellationToken = default) where T : Table
			=> GetPageByQueryAsync<T>(skip, take, query, cancellationToken);

		public Task<Page<T>> GetPageByQueryAsync<T>(int skip, int take, string query = null, CancellationToken cancellationToken = default) where T : Table
			=> GetPageByQueryInternalAsync<T>(skip, take, Table.GetTableName<T>(), query, null, null, cancellationToken);

		private Task<Page<T>> GetPageByQueryInternalAsync<T>(int skip, int take, string tableName, string query, List<string> fieldList, string extraQueryString, CancellationToken cancellationToken)
		{
			_logger.LogTrace($"Entered {nameof(GetPageByQueryInternalAsync)}" +
							 $" type: {typeof(T)}" +
							 $", {nameof(tableName)}: {tableName}" +
							 $", {nameof(query)}: {query ?? "<not set>"}" +
							 $", {nameof(fieldList)}: {(fieldList?.Any() == true ? string.Join(", ", fieldList) : "<not set>")}" +
							 $", {nameof(skip)}: {skip}" +
							 $", {nameof(take)}: {take}" +
							 ".");

			return GetInternalAsync<Page<T>>(
				$"api/now/table/{tableName}" +
				$"?sysparm_offset={skip}" +
				$"&sysparm_limit={take}" +
				(!string.IsNullOrWhiteSpace(query) ? $"&sysparm_query={HttpUtility.UrlEncode(query)}" : null) +
				(fieldList?.Any() == true ? "&" : "") +
				BuildFieldListQueryParameter(fieldList) +
				(string.IsNullOrWhiteSpace(extraQueryString) ? "" : "&" + extraQueryString
			), cancellationToken);
		}

		private static string BuildFieldListQueryParameter(List<string> fieldList)
			=> fieldList?.Any() == true ? $"sysparm_fields={HttpUtility.UrlEncode(string.Join(",", fieldList))}" : null;

		[Obsolete("Use GetByIdAsync instead.")]
		public Task<T> GetById<T>(string sysId, CancellationToken cancellationToken = default) where T : Table
			=> GetByIdAsync<T>(sysId, cancellationToken);

		public async Task<T> GetByIdAsync<T>(string sysId, CancellationToken cancellationToken = default) where T : Table
			=> (await GetInternalAsync<RestResponse<T>>($"api/now/table/{Table.GetTableName<T>()}/{sysId}", cancellationToken).ConfigureAwait(false)).Item;

		[Obsolete("Use GetByIdAsync instead.")]
		public Task<JObject> GetById(string tableName, string sysId, CancellationToken cancellationToken = default)
			=> GetByIdAsync(tableName, sysId, cancellationToken);

		public async Task<JObject> GetByIdAsync(string tableName, string sysId, CancellationToken cancellationToken = default)
			=> (await GetInternalAsync<RestResponse<JObject>>($"api/now/table/{tableName}/{sysId}", cancellationToken).ConfigureAwait(false)).Item;

		[Obsolete("Use GetAttachmentsAsync instead.")]
		public Task<List<Attachment>> GetAttachments<T>(T table, CancellationToken cancellationToken = default) where T : Table
			=> GetAttachmentsAsync<T>(table, cancellationToken);

		/// <summary>
		/// Get attachments for a given Table based entry
		/// </summary>
		/// <typeparam name="T">The type of object</typeparam>
		/// <param name="table">The object itself</param>
		/// <returns>A list of attachments</returns>
		public async Task<List<Attachment>> GetAttachmentsAsync<T>(T table, CancellationToken cancellationToken = default) where T : Table
			=> (await GetInternalAsync<RestResponse<List<Attachment>>>($"api/now/attachment?sysparm_query=table_name={Table.GetTableName<T>()}^table_sys_id={table.SysId}", cancellationToken).ConfigureAwait(false)).Item;

		[Obsolete("Use GetAttachmentsAsync instead.")]
		public Task<List<Attachment>> GetAttachments(string tableName, string tableSysId, CancellationToken cancellationToken = default)
			=> GetAttachmentsAsync(tableName, tableSysId, cancellationToken);

		/// <summary>
		/// Get attachments for a given Table based entry
		/// </summary>
		/// <param name="tableName">The name of the table</param>
		/// <param name="tableSysId">The sys_id of the entry in the referenced table</param>
		/// <returns>A list of attachments</returns>
		public async Task<List<Attachment>> GetAttachmentsAsync(string tableName, string tableSysId, CancellationToken cancellationToken = default)
			=> (await GetInternalAsync<RestResponse<List<Attachment>>>($"api/now/attachment?sysparm_query=table_name={tableName}^table_sys_id={tableSysId}", cancellationToken).ConfigureAwait(false)).Item;

		[Obsolete("Use DownloadAttachmentAsync instead.")]
		public Task<string> DownloadAttachment(Attachment attachment, string outputPath, string filename = null, CancellationToken cancellationToken = default)
			=> DownloadAttachmentAsync(attachment, outputPath, filename, cancellationToken);

		/// <summary>
		/// Download a specified attachment to the local file system
		/// </summary>
		/// <param name="attachment">The attachment to download</param>
		/// <param name="outputPath">The path to store the attachment content in</param>
		/// <param name="filename">Optional filename for the file, defaults to filename from ServiceNow if unspecified</param>
		/// <returns>The path of the downloaded file</returns>

		public async Task<string> DownloadAttachmentAsync(Attachment attachment, string outputPath, string filename = null, CancellationToken cancellationToken = default)
		{
			filename = filename ?? attachment.FileName;
			var fileToWriteTo = Path.Combine(outputPath, filename);
			//wc.DownloadProgressChanged += wc_DownloadProgressChanged;
			//await _httpClient..DownloadFile(new Uri(attachment.DownloadLink), localPath);
			using (var response = await _httpClient.GetAsync(attachment.DownloadLink, cancellationToken).ConfigureAwait(false))
			{
				using (var streamToReadFrom = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
				{
					using (Stream streamToWriteTo = File.Open(fileToWriteTo, FileMode.Create))
					{
						await streamToReadFrom.CopyToAsync(streamToWriteTo).ConfigureAwait(false);
					}

					response.Content = null;
				}
			}

			return fileToWriteTo;
		}

		[Obsolete("Use CreateAsync instead.")]
		public Task<T> Create<T>(T @object, CancellationToken cancellationToken = default) where T : Table
			=> CreateAsync<T>(@object, cancellationToken);

		public async Task<T> CreateAsync<T>(T @object, CancellationToken cancellationToken = default) where T : Table
		{
			// https://docs.servicenow.com/bundle/kingston-application-development/page/integrate/inbound-rest/concept/c_TableAPI.html#ariaid-title6
			var serializedObject = JsonConvert.SerializeObject(@object, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
			HttpContent content = new StringContent(serializedObject, null, "application/json");
			var tableName = Table.GetTableName<T>();
			using (var response = await _httpClient.PostAsync($"api/now/table/{tableName}", content, cancellationToken).ConfigureAwait(false))
			{
				if (response == null)
				{
					throw new Exception("Null response.");
				}

				if (!response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					throw new Exception($"Server error {response.StatusCode} ({(int)response.StatusCode}): {response.ReasonPhrase} - {responseContent}.");
				}

				var deserializeObject = await GetDeserializedObjectFromResponse<RestResponse<T>>(response, Guid.NewGuid()).ConfigureAwait(false);
				return deserializeObject.Item;
			}
		}

		[Obsolete("Use CreateAsync instead.")]
		public Task<JObject> Create(string tableName, JObject jObject, CancellationToken cancellationToken = default)
			=> CreateAsync(tableName, jObject, cancellationToken);

		public async Task<JObject> CreateAsync(string tableName, JObject jObject, CancellationToken cancellationToken = default)
		{
			// https://docs.servicenow.com/bundle/kingston-application-development/page/integrate/inbound-rest/concept/c_TableAPI.html#ariaid-title6
			var serializedObject = JsonConvert.SerializeObject(jObject, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
			HttpContent content = new StringContent(serializedObject, null, "application/json");
			using (var response = await _httpClient.PostAsync($"api/now/table/{tableName}", content, cancellationToken).ConfigureAwait(false))
			{
				if (response == null)
				{
					throw new Exception("Null response.");
				}

				if (!response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					throw new Exception($"Server error {response.StatusCode} ({(int)response.StatusCode}): {response.ReasonPhrase} - {responseContent}.");
				}

				var deserializeObject = await GetDeserializedObjectFromResponse<RestResponse<JObject>>(response, Guid.NewGuid()).ConfigureAwait(false);
				return deserializeObject.Item;
			}
		}

		[Obsolete("Use UpdateAsync instead.")]
		public Task<JObject> Update(string tableName, JObject jObject, CancellationToken cancellationToken = default)
			=> UpdateAsync(tableName, jObject, cancellationToken);

		public async Task<JObject> UpdateAsync(string tableName, JObject jObject, CancellationToken cancellationToken = default)
		{
			if (jObject == null)
			{
				throw new ArgumentNullException(nameof(jObject));
			}
			if (!jObject.TryGetValue("sys_id", out var sysId))
			{
				throw new ArgumentException($"sys_id must be present in the {nameof(jObject)} parameter.", nameof(jObject));
			}

			// https://docs.servicenow.com/bundle/kingston-application-development/page/integrate/inbound-rest/concept/c_TableAPI.html#ariaid-title6
			var serializedObject = JsonConvert.SerializeObject(jObject, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
			HttpContent content = new StringContent(serializedObject, null, "application/json");
			using (var response = await _httpClient.PutAsync($"api/now/table/{tableName}/{sysId}", content, cancellationToken).ConfigureAwait(false))
			{
				if (response == null)
				{
					throw new Exception("Null response.");
				}

				if (!response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					throw new Exception($"Server error {response.StatusCode} ({(int)response.StatusCode}): {response.ReasonPhrase} - {responseContent}.");
				}

				var deserializeObject = await GetDeserializedObjectFromResponse<RestResponse<JObject>>(response, Guid.NewGuid()).ConfigureAwait(false);
				return deserializeObject.Item;
			}
		}

		[Obsolete("Use PatchAsync instead.")]
		public Task<JObject> Patch(string tableName, JObject jObject, CancellationToken cancellationToken = default)
			=> PatchAsync(tableName, jObject, cancellationToken);

		/// <summary>
		/// Patches an existing entry. jObject must contain sys_id
		/// </summary>
		/// <param name="tableName">The table to update an entry in</param>
		/// <param name="jObject">The object details, sys_id must be set</param>
		/// <param name="cancellationToken"></param>
		public async Task<JObject> PatchAsync(string tableName, JObject jObject, CancellationToken cancellationToken = default)
		{
			if (jObject == null)
			{
				throw new ArgumentNullException(nameof(jObject));
			}
			if (!jObject.TryGetValue("sys_id", out var sysId))
			{
				throw new ArgumentException($"sys_id must be present in the {nameof(jObject)} parameter.", nameof(jObject));
			}

			var serializedObject = JsonConvert.SerializeObject(jObject, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
			var request = new HttpRequestMessage(new HttpMethod("PATCH"), $"api/now/table/{tableName}/{sysId}") { Content = new StringContent(serializedObject, null, "application/json") };

			using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
			{
				if (response == null)
				{
					throw new Exception("Null response.");
				}

				if (!response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					throw new Exception($"Server error {response.StatusCode} ({(int)response.StatusCode}): {response.ReasonPhrase} - {responseContent}.");
				}

				var deserializeObject = await GetDeserializedObjectFromResponse<RestResponse<JObject>>(response, Guid.NewGuid()).ConfigureAwait(false);
				return deserializeObject.Item;
			}
		}

		[Obsolete("Use DeleteAsync instead.")]
		public Task Delete(string tableName, string sysId, CancellationToken cancellationToken = default)
			=> DeleteAsync(tableName, sysId, cancellationToken);

		public async Task DeleteAsync(string tableName, string sysId, CancellationToken cancellationToken = default)
		{
			// https://docs.servicenow.com/bundle/kingston-application-development/page/integrate/inbound-rest/concept/c_TableAPI.html#ariaid-title6
			using (var response = await _httpClient.DeleteAsync($"api/now/table/{tableName}/{sysId}", cancellationToken).ConfigureAwait(false))
			{
				if (response == null)
				{
					throw new Exception("Null response.");
				}

				if (!response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					throw new Exception($"Server error {response.StatusCode} ({(int)response.StatusCode}): {response.ReasonPhrase} - {responseContent}.");
				}
			}
		}

		[Obsolete("Use UpdateAsync instead.")]
		public Task Update<T>(T @object, CancellationToken cancellationToken = default) where T : Table
			=> UpdateAsync<T>(@object, cancellationToken);

		public async Task UpdateAsync<T>(T @object, CancellationToken cancellationToken = default) where T : Table
		{
			HttpContent content = new StringContent(JsonConvert.SerializeObject(@object), null, "application/json");
			var tableName = Table.GetTableName<T>();
			using (var response = await _httpClient.PutAsync($"api/now/table/{tableName}/{@object.SysId}", content, cancellationToken).ConfigureAwait(false))
			{
				if (response == null)
				{
					throw new Exception("Null response.");
				}

				if (!response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					throw new Exception($"Server error {response.StatusCode} ({(int)response.StatusCode}): {response.ReasonPhrase} - {responseContent}.");
				}
			}
		}

		[Obsolete("Use DeleteAsync instead.")]
		public Task Delete<T>(string sysId, CancellationToken cancellationToken = default) where T : Table
			=> DeleteAsync<T>(sysId, cancellationToken);

		public async Task DeleteAsync<T>(string sysId, CancellationToken cancellationToken = default) where T : Table
		{
			var tableName = Table.GetTableName<T>();
			using (var response = await _httpClient.DeleteAsync($"api/now/table/{tableName}/{sysId}", cancellationToken).ConfigureAwait(false))
			{
				if (response == null)
				{
					throw new Exception("Null response.");
				}

				if (!response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					throw new Exception($"Server error {response.StatusCode} ({(int)response.StatusCode}): {response.ReasonPhrase} - {responseContent}.");
				}
			}
		}

		[Obsolete("Use GetMetaForClassAsync instead.")]
		public Task<RestResponse<MetaDataResult>> GetMetaForClass(string className, CancellationToken cancellationToken = default)
			=> GetMetaForClass(className, cancellationToken);

		public Task<RestResponse<MetaDataResult>> GetMetaForClassAsync(string className, CancellationToken cancellationToken = default)
			=> GetInternalAsync<RestResponse<MetaDataResult>>($"api/now/cmdb/meta/{className}", cancellationToken);

		private async Task<T> GetInternalAsync<T>(string subUrl, CancellationToken cancellationToken)
		{
			var requestId = Guid.NewGuid();
			_logger.LogTrace($"Request {requestId}: Entered {nameof(GetInternalAsync)} {nameof(subUrl)}: {subUrl}");
			var sw = Stopwatch.StartNew();
			using (var response = await _httpClient.GetAsync(subUrl, cancellationToken).ConfigureAwait(false))
			{
				_logger.LogTrace($"Request {requestId}: GetAsync took {sw.Elapsed}");

				if (response == null)
				{
					throw new Exception("Null response.");
				}

				if (!response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
					throw new Exception($"Server error {response.StatusCode} ({(int)response.StatusCode}): {response.ReasonPhrase} - {responseContent}.");
				}

				return await GetDeserializedObjectFromResponse<T>(response, requestId).ConfigureAwait(false);
			}
		}

		private async Task<T> GetDeserializedObjectFromResponse<T>(HttpResponseMessage response, Guid requestId)
		{
			string content = null;
			try
			{
				content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				_logger.LogTrace($"Request {requestId}: Content length: {content.Length / 1024:N0} KB");

				// Deserialize the object
				var deserializeObject = JsonConvert.DeserializeObject<T>(content);

				// If this is a list then we add on the TotalCount
				if (deserializeObject is RestListResponseBase restListResponse
					&& response.Headers.TryGetValues("X-Total-Count", out var values)
					)
				{
					// We really should have an X-Total-Count available when retrieving lists
					var totalCount = values.FirstOrDefault();
					// If we really don't have it then it will default to 0 as this is an int
					if (totalCount != null && int.TryParse(totalCount, out var totalCountInt))
					{
						restListResponse.TotalCount = totalCountInt;
						_logger.LogTrace($"Request {requestId}: TotalCount: {totalCountInt:N0}");
					}
				}

				return deserializeObject;
			}
			catch (Exception e)
			{
				throw new ServiceNowApiException($"A problem occurred deserializing the content from the response. Content:\n{content ?? "<content not read>"}", e);
			}
		}

		[Obsolete("Use GetLinkedEntityAsync instead.")]
		public Task<JObject> GetLinkedEntity(string link, List<string> fieldList, CancellationToken cancellationToken = default)
			=> GetLinkedEntityAsync(link, fieldList, cancellationToken);

		public async Task<JObject> GetLinkedEntityAsync(string link, List<string> fieldList, CancellationToken cancellationToken = default)
		{
			var linkWithFields = link.Substring(link.IndexOf("/api/", StringComparison.Ordinal) + 1);
			if (fieldList?.Any() == true)
			{
				linkWithFields += "?" + BuildFieldListQueryParameter(fieldList);
			}

			return (await GetInternalAsync<RestResponse<JObject>>(linkWithFields, cancellationToken).ConfigureAwait(false)).Item;
		}
	}
}