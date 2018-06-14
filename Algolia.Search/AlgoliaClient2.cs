using Algolia.Search;
using Algolia.Search.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public class AlgoliaClient2 : AlgoliaClient
{
    public AlgoliaClient2(string applicationId, string apiKey, IEnumerable<string> hosts = null, HttpMessageHandler mock = null) : base(applicationId, apiKey, hosts, mock)
    {
    }

    public override async Task<JObject> ExecuteRequest(callType type, string method, string requestUrl, object content, CancellationToken token, RequestOptions requestOptions)
    {
        string[] hosts = null;
        string requestExtraQueryParams = "";
        HttpClient client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }); 
        if (type == callType.Search)
        {
            hosts = filterOnActiveHosts(_readHosts, true);
            foreach (var header in _searchHttpClient.DefaultRequestHeaders)
            {
                client.DefaultRequestHeaders.Add(header.Key, header.Value);
            }
            client.Timeout = _searchHttpClient.Timeout;
        }
        else
        {
            hosts = type == callType.Read
                ? filterOnActiveHosts(_readHosts, true)
                : filterOnActiveHosts(_writeHosts, false);
            foreach (var header in _buildHttpClient.DefaultRequestHeaders)
            {
                client.DefaultRequestHeaders.Add(header.Key, header.Value);
            }
            client.Timeout = _buildHttpClient.Timeout;
        }

        if (requestOptions != null)
        {
            requestExtraQueryParams = buildExtraQueryParamsUrlString(requestOptions.GenerateExtraQueryParams());
        }

        Dictionary<string, string> errors = new Dictionary<string, string>();
        foreach (string host in hosts)
        {
            try
            {
                try
                {
                    string url;
                    if (String.IsNullOrEmpty(requestExtraQueryParams))
                    {
                        url = string.Format("https://{0}{1}", host, requestUrl);
                    }
                    else
                    {
                        url = requestUrl.Contains("?") // check if we already had query parameters added to the requestUrl
                            ? string.Format("https://{0}{1}&{2}", host, requestUrl, requestExtraQueryParams)
                            : string.Format("https://{0}{1}?{2}", host, requestUrl, requestExtraQueryParams);
                    }

                    HttpRequestMessage httpRequestMessage = null;
                    switch (method)
                    {
                        case "GET":
                            httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                            break;
                        case "POST":
                            httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, url);
                            if (content != null)
                            {
                                httpRequestMessage.Content =
                                    new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(content, new Newtonsoft.Json.Converters.StringEnumConverter()));
                            }
                            break;
                        case "PUT":
                            httpRequestMessage = new HttpRequestMessage(HttpMethod.Put, url);
                            if (content != null)
                            {
                                httpRequestMessage.Content =
                                    new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(content, new Newtonsoft.Json.Converters.StringEnumConverter()));
                            }
                            break;
                        case "DELETE":
                            httpRequestMessage = new HttpRequestMessage(HttpMethod.Delete, url);
                            break;
                    }

                    if (requestOptions != null)
                    {
                        foreach (var header in requestOptions.GenerateExtraHeaders())
                        {
                            httpRequestMessage.Headers.Add(header.Key, header.Value);
                        }
                    }

                    HttpResponseMessage responseMsg = await client.SendAsync(httpRequestMessage, token)
                        .ConfigureAwait(_continueOnCapturedContext);

                    if (responseMsg.IsSuccessStatusCode)
                    {
                        string serializedJSON = await responseMsg.Content.ReadAsStringAsync().ConfigureAwait(_continueOnCapturedContext);
                        JObject obj = JObject.Parse(serializedJSON);
                        if (type == callType.Search || type == callType.Read)
                        {
                            _readHostsStatus[host] = setHostStatus(true);
                        }
                        else
                        {
                            _writeHostsStatus[host] = setHostStatus(true);
                        }
                        return obj;
                    }
                    else
                    {
                        string serializedJSON = await responseMsg.Content.ReadAsStringAsync().ConfigureAwait(_continueOnCapturedContext);
                        string message = "Internal Error";
                        string status = "0";
                        try
                        {
                            JObject obj = JObject.Parse(serializedJSON);
                            message = obj["message"].ToString();
                            status = obj["status"].ToString();
                            if (obj["status"].ToObject<int>() / 100 == 4)
                            {
                                throw new AlgoliaException(message);
                            }
                        }
                        catch (JsonReaderException)
                        {
                            message = responseMsg.ReasonPhrase;
                            status = "0";
                        }

                        errors.Add(host + '(' + status + ')', message);
                    }
                }
                catch (AlgoliaException)
                {
                    if (type == callType.Search || type == callType.Read)
                    {
                        _readHostsStatus[host] = setHostStatus(false);
                    }
                    else
                    {
                        _writeHostsStatus[host] = setHostStatus(false);
                    }
                    throw;
                }
                catch (TaskCanceledException e)
                {
                    if (token.IsCancellationRequested)
                    {
                        throw e;
                    }
                    if (type == callType.Search || type == callType.Read)
                    {
                        _readHostsStatus[host] = setHostStatus(false);
                    }
                    else
                    {
                        _writeHostsStatus[host] = setHostStatus(false);
                    }
                    errors.Add(host, "Timeout expired");
                }
                catch (Exception ex)
                {
                    if (type == callType.Search || type == callType.Read)
                    {
                        _readHostsStatus[host] = setHostStatus(false);
                    }
                    else
                    {
                        _writeHostsStatus[host] = setHostStatus(false);
                    }
                    errors.Add(host, ex.Message);
                }

            }
            catch (AlgoliaException)
            {
                throw;
            }

        }
        throw new AlgoliaException("Hosts unreachable: " + string.Join(", ", errors.Select(x => x.Key + "=" + x.Value).ToArray()));
    }
}