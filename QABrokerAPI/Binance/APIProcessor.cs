
using log4net;
using Newtonsoft.Json;
using QABrokerAPI.Common.Caching.Interfaces;
using QABrokerAPI.Common.Enums;
using QABrokerAPI.Common.Models.Response.Error;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace QABrokerAPI.Binance
{
    public class APIProcessor : IAPIProcessor
    {
        private readonly string _apiKey;
        private readonly string _secretKey;
        private IAPICacheManager _apiCache;
        private ILog _logger;
        private bool _cacheEnabled;
        private TimeSpan _cacheTime;

        public APIProcessor(string apiKey, string secretKey, IAPICacheManager apiCache)
        {
            this._apiKey = apiKey;
            this._secretKey = secretKey;
            if (apiCache != null)
            {
                this._apiCache = apiCache;
                this._cacheEnabled = true;
            }
            this._logger = LogManager.GetLogger(typeof(APIProcessor));
            this._logger.Debug((object)$"API Processor set up. Cache Enabled={this._cacheEnabled}");
        }

        public void SetCacheTime(TimeSpan time) => this._cacheTime = time;

        private async Task<T> HandleResponse<T>(
          HttpResponseMessage message,
          string requestMessage,
          string fullCacheKey)
          where T : class
        {
            if (message.IsSuccessStatusCode)
            {
                string str = await message.Content.ReadAsStringAsync();
                T obj1 = default(T);
                T obj2;
                try
                {
                    obj2 = JsonConvert.DeserializeObject<T>(str);
                }
                catch (Exception ex)
                {
                    string message1 = $"Unable to deserialize message from: {requestMessage}. Exception: {ex.Message}";
                    this._logger.Error((object)message1);
                    throw new BrokerException(message1, new BrokerError()
                    {
                        RequestMessage = requestMessage,
                        Message = ex.Message
                    });
                }
                this._logger.Debug((object)("Successful Message Response=" + str));
                if ((object)obj2 == null)
                    throw new Exception("Unable to deserialize to provided type");
                if (this._apiCache.Contains(fullCacheKey))
                    this._apiCache.Remove(fullCacheKey);
                this._apiCache.Add<T>(obj2, fullCacheKey, this._cacheTime);
                return obj2;
            }
            string str1 = await message.Content.ReadAsStringAsync();
            BrokerError errorObject = JsonConvert.DeserializeObject<BrokerError>(str1);
            if (errorObject == null)
                throw new BrokerException("Unexpected Error whilst handling the response", (BrokerError)null);
            errorObject.RequestMessage = requestMessage;
            BrokerException BrokerException = this.CreateBrokerException(message.StatusCode, errorObject);
            this._logger.Error((object)("Error Message Received:" + str1), (Exception)BrokerException);
            throw BrokerException;
        }

        private BrokerException CreateBrokerException(
          HttpStatusCode statusCode,
          BrokerError errorObject)
        {
            if (statusCode == HttpStatusCode.GatewayTimeout)
                return (BrokerException)new BrokerTimeoutException(errorObject);
            int num = (int)statusCode;
            if (num >= 400 && num <= 500)
                return (BrokerException)new BrokerBadRequestException(errorObject);
            return num < 500 ? new BrokerException("Binance API Error", errorObject) : (BrokerException)new BrokerServerException(errorObject);
        }

        private bool CheckAndRetrieveCachedItem<T>(string fullKey, out T item) where T : class
        {
            item = default(T);
            bool flag = this._apiCache.Contains(fullKey);
            item = flag ? this._apiCache.Get<T>(fullKey) : default(T);
            return flag;
        }

        public async Task<T> ProcessGetRequest<T>(BrokerEndpointData endpoint, int receiveWindow = 5000) where T : class
        {
            string fullKey = $"{typeof(T).Name}-{endpoint.Uri.AbsoluteUri}";
            T request1;
            if (this._cacheEnabled && endpoint.UseCache && this.CheckAndRetrieveCachedItem<T>(fullKey, out request1))
                return request1;
            HttpResponseMessage request2;
            switch (endpoint.SecurityType)
            {
                case EndpointSecurityType.None:
                case EndpointSecurityType.ApiKey:
                    request2 = await RequestClient.GetRequest(endpoint.Uri);
                    break;
                case EndpointSecurityType.Signed:
                    request2 = await RequestClient.SignedGetRequest(endpoint.Uri, this._apiKey, this._secretKey, endpoint.Uri.Query, (long)receiveWindow);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return await this.HandleResponse<T>(request2, endpoint.ToString(), fullKey);
        }

        public async Task<T> ProcessDeleteRequest<T>(BrokerEndpointData endpoint, int receiveWindow = 5000) where T : class
        {
            string fullKey = $"{typeof(T).Name}-{endpoint.Uri.AbsoluteUri}";
            T obj;
            if (this._cacheEnabled && endpoint.UseCache && this.CheckAndRetrieveCachedItem<T>(fullKey, out obj))
                return obj;
            HttpResponseMessage message;
            switch (endpoint.SecurityType)
            {
                case EndpointSecurityType.ApiKey:
                    message = await RequestClient.DeleteRequest(endpoint.Uri);
                    break;
                case EndpointSecurityType.Signed:
                    message = await RequestClient.SignedDeleteRequest(endpoint.Uri, this._apiKey, this._secretKey, endpoint.Uri.Query, (long)receiveWindow);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return await this.HandleResponse<T>(message, endpoint.ToString(), fullKey);
        }

        public async Task<T> ProcessPostRequest<T>(BrokerEndpointData endpoint, int receiveWindow = 5000) where T : class
        {
            string fullKey = $"{typeof(T).Name}-{endpoint.Uri.AbsoluteUri}";
            T obj;
            if (this._cacheEnabled && endpoint.UseCache && this.CheckAndRetrieveCachedItem<T>(fullKey, out obj))
                return obj;
            HttpResponseMessage message;
            switch (endpoint.SecurityType)
            {
                case EndpointSecurityType.None:
                    throw new ArgumentOutOfRangeException();
                case EndpointSecurityType.ApiKey:
                    message = await RequestClient.PostRequest(endpoint.Uri);
                    break;
                case EndpointSecurityType.Signed:
                    message = await RequestClient.SignedPostRequest(endpoint.Uri, this._apiKey, this._secretKey, endpoint.Uri.Query, (long)receiveWindow);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return await this.HandleResponse<T>(message, endpoint.ToString(), fullKey);
        }

        public async Task<T> ProcessPutRequest<T>(BrokerEndpointData endpoint, int receiveWindow = 5000) where T : class
        {
            string fullKey = $"{typeof(T).Name}-{endpoint.Uri.AbsoluteUri}";
            T obj;
            if (this._cacheEnabled && endpoint.UseCache && this.CheckAndRetrieveCachedItem<T>(fullKey, out obj))
                return obj;
            switch (endpoint.SecurityType)
            {
                case EndpointSecurityType.ApiKey:
                    return await this.HandleResponse<T>(await RequestClient.PutRequest(endpoint.Uri), endpoint.ToString(), fullKey);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
