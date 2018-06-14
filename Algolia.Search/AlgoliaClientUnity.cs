using Algolia.Search;
using Algolia.Search.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class AlgoliaClientUnity : AlgoliaClient
{
    public AlgoliaClientUnity(string applicationId, string apiKey, IEnumerable<string> hosts = null, HttpMessageHandler mock = null) : base(applicationId, apiKey, hosts, mock)
    {
    }

    public override async Task<JObject> ExecuteRequest(callType type, string method, string requestUrl, object content, CancellationToken token, RequestOptions requestOptions)
    {
        string[] hosts = null;
        string requestExtraQueryParams = "";
        if (type == callType.Search)
        {
            hosts = filterOnActiveHosts(_readHosts, true);
        }
        else
        {
            hosts = type == callType.Read
                ? filterOnActiveHosts(_readHosts, true)
                : filterOnActiveHosts(_writeHosts, false);
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

                    UnityWebRequest www = null;
                    string bodyData = null;
                    if(content != null)
                        bodyData = JsonConvert.SerializeObject(content, new Newtonsoft.Json.Converters.StringEnumConverter());;
                    switch (method)
                    {
                        case "GET":
                            www = UnityWebRequest.Get(url);
                            break;
                        case "POST": // UnityWebRequest.Post() doesn't work well for some reason.
                        case "PUT":
                            www = UnityWebRequest.Put(url, bodyData);
                            break;
                        case "DELETE":
                            www = UnityWebRequest.Delete(url);
                            break;
                    }
                    www.method = method;

                    www.SetRequestHeader("X-Algolia-Application-Id", _applicationId);
                    www.SetRequestHeader("X-Algolia-API-Key", _apiKey);
                    www.SetRequestHeader("User-Agent", "Algolia for Csharp 4.0.0");
                    www.SetRequestHeader("Accept", "application/json");

                    if (requestOptions != null)
                    {
                        foreach (var header in requestOptions.GenerateExtraHeaders())
                        {
                            www.SetRequestHeader(header.Key, header.Value);
                        }
                    }

                    www.timeout = (int)Math.Max(1, type == callType.Search ? _searchTimeout : _writeTimeout);

                    UnityWebRequestAsyncOperation asyncOp = www.SendWebRequest();

                    // To avoid deadlock when called from UI thread with .GetAwaiter().GetResult()
                    // set runSynchronously = true;
                    bool runSynchronously = false;
                    if(runSynchronously)
                    {
                        while(!www.isDone) { }
                    }
                    else
                    {
                        var tcs = new TaskCompletionSource<AsyncOperation>();

                        using (token.Register(() => {
                            tcs.TrySetCanceled();
                        }))
                        {
                            asyncOp.completed += (AsyncOperation op) => tcs.TrySetResult(op);
                            // Not using _continueOnCapturedContext, but staying on main thread when done (assumes called from main thread)
                            await tcs.Task;
                        }
                    }
                    
                    if (www.responseCode >= 200 && www.responseCode < 300)
                    {
                        JObject obj = JObject.Parse(www.downloadHandler.text);
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
                        string message = "Internal Error";
                        string status = "0";
                        try
                        {
                            JObject obj = JObject.Parse(www.downloadHandler.text);
                            message = obj["message"].ToString();
                            status = obj["status"].ToString();
                            if (obj["status"].ToObject<int>() / 100 == 4)
                            {
                                throw new AlgoliaException(message);
                            }
                        }
                        catch (JsonReaderException)
                        {
                            message = www.error;
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
