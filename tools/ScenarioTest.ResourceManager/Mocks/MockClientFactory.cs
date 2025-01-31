﻿// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

using Hyak.Common;
using Microsoft.Azure;
using Microsoft.Azure.Commands.Common.Authentication;
using Microsoft.Azure.Commands.Common.Authentication.Abstractions;
using Microsoft.Azure.Commands.Common.Authentication.Factories;
using Microsoft.Azure.Commands.Common.Authentication.Models;
using Microsoft.Azure.Test.HttpRecorder;
using Microsoft.Rest.Azure;
using Microsoft.WindowsAzure.Commands.ScenarioTest;
using Microsoft.WindowsAzure.Commands.Utilities.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
#if NETSTANDARD
using Microsoft.Azure.Commands.Common.Authentication.Abstractions.Core;
#endif

namespace Microsoft.WindowsAzure.Commands.Common.Test.Mocks
{
    public class MockClientFactory : IClientFactory
    {
        private readonly bool throwWhenNotAvailable;

        public bool MoqClients { get; set; }

        public List<object> ManagementClients { get; private set; }

        public MockClientFactory(IEnumerable<object> clients, bool throwIfClientNotSpecified = true)
        {
            UniqueUserAgents = new HashSet<ProductInfoHeaderValue>();
            ManagementClients = clients.ToList();
            throwWhenNotAvailable = throwIfClientNotSpecified;
        }

        public TClient CreateClient<TClient>(IAzureContext context, string endpoint) where TClient : ServiceClient<TClient>
        {
            Debug.Assert(context != null);

            SubscriptionCloudCredentials creds = AzureSession.Instance.AuthenticationFactory
                        .GetSubscriptionCloudCredentials(
                            context,
                            AzureEnvironment.Endpoint.ResourceManager);
            TClient client = CreateCustomClient<TClient>(creds, context.Environment.GetEndpointAsUri(endpoint));

            return client;
        }

        public TClient CreateClient<TClient>(IAzureContextContainer profile, string endpoint) where TClient : ServiceClient<TClient>
        {
            return CreateClient<TClient>(profile, profile.DefaultContext.Subscription, endpoint);
        }

        public TClient CreateClient<TClient>(IAzureContextContainer profile, IAzureSubscription subscription, string endpoint) where TClient : ServiceClient<TClient>
        {
#if !NETSTANDARD
            if (subscription == null)
            {
                throw new ArgumentException(Microsoft.Azure.Commands.ResourceManager.Common.Properties.Resources.InvalidDefaultSubscription);
            }

            if (profile == null)
            {
                profile = new AzureSMProfile(Path.Combine(AzureSession.Instance.ProfileDirectory, AzureSession.Instance.ProfileFile));
            }

            SubscriptionCloudCredentials creds = new TokenCloudCredentials(subscription.Id.ToString(), "fake_token");
            if (HttpMockServer.GetCurrentMode() != HttpRecorderMode.Playback)
            {
                ProfileClient profileClient = new ProfileClient(profile as AzureSMProfile);
                AzureContext context = new AzureContext(
                    subscription,
                    profileClient.GetAccount(subscription.GetAccount()),
                    profileClient.GetEnvironmentOrDefault(subscription.GetEnvironment())
                );

                creds = AzureSession.Instance.AuthenticationFactory.GetSubscriptionCloudCredentials(context);
            }

            Uri endpointUri = profile.Environments.FirstOrDefault((e) => e.Name.Equals(subscription.GetEnvironment(), StringComparison.OrdinalIgnoreCase)).GetEndpointAsUri(endpoint);
            return CreateCustomClient<TClient>(creds, endpointUri);
#else
            throw new NotSupportedException("AzureSMProfile is not supported in Azure PS on .Net Core.");
#endif
        }

        public TClient CreateCustomClient<TClient>(params object[] parameters) where TClient : ServiceClient<TClient>
        {
            TClient client = ManagementClients.FirstOrDefault(o => o is TClient) as TClient;
            if (client == null)
            {
                if (throwWhenNotAvailable)
                {
                    throw new ArgumentException(
                        string.Format("TestManagementClientHelper class wasn't initialized with the {0} client.",
                            typeof(TClient).Name));
                }
                else
                {
                    var realClientFactory = new ClientFactory();
                    var realClient = realClientFactory.CreateCustomClient<TClient>(parameters);
                    var newRealClient = realClient.WithHandler(HttpMockServer.CreateInstance());

                    var initialTimeoutPropInfo = typeof(TClient).GetProperty("LongRunningOperationInitialTimeout", BindingFlags.Public | BindingFlags.Instance);
                    if (initialTimeoutPropInfo != null && initialTimeoutPropInfo.CanWrite)
                    {
                        initialTimeoutPropInfo.SetValue(newRealClient, 0, null);
                    }

                    var retryTimeoutPropInfo = typeof(TClient).GetProperty("LongRunningOperationRetryTimeout", BindingFlags.Public | BindingFlags.Instance);
                    if (retryTimeoutPropInfo != null && retryTimeoutPropInfo.CanWrite)
                    {
                        retryTimeoutPropInfo.SetValue(newRealClient, 0, null);
                    }

                    realClient.Dispose();
                    return newRealClient;
                }
            }
            else
            {
                if (!MoqClients && !client.GetType().Namespace.Contains("Castle."))
                {
                    // Use the WithHandler method to create an extra reference to the http client
                    // this will prevent the httpClient from being disposed in a long-running test using 
                    // the same client for multiple cmdlets
                    client = client.WithHandler(new PassThroughDelegatingHandler());
                }
            }

            return client;
        }

        public HttpClient CreateHttpClient(string endpoint, ICredentials credentials)
        {
            return CreateHttpClient(endpoint, ClientFactory.CreateHttpClientHandler(endpoint, credentials));
        }

        public HttpClient CreateHttpClient(string serviceUrl, HttpMessageHandler effectiveHandler)
        {
            if (serviceUrl == null)
            {
                throw new ArgumentNullException("serviceUrl");
            }
            if (effectiveHandler == null)
            {
                throw new ArgumentNullException("effectiveHandler");
            }
            var mockHandler = HttpMockServer.CreateInstance();
            mockHandler.InnerHandler = effectiveHandler;

            HttpClient client = new HttpClient(mockHandler)
            {
                BaseAddress = new Uri(serviceUrl),
                MaxResponseContentBufferSize = 30 * 1024 * 1024
            };

            client.DefaultRequestHeaders.Accept.Clear();

            return client;
        }

        public void AddAction(IClientAction action)
        {
            // Do nothing
        }

        public void RemoveAction(Type actionType)
        {
            // Do nothing
        }

        public void AddHandler<T>(T handler) where T : DelegatingHandler, ICloneable
        {
            // Do nothing
        }

        public void RemoveHandler(Type handlerType)
        {
            // Do nothing
        }

        public DelegatingHandler[] GetCustomHandlers()
        {
            // the equivalent of doing nothing
            return new DelegatingHandler[0];
        }

        public void AddUserAgent(string productName, string productVersion)
        {
            if (string.IsNullOrEmpty(productName))
            {
                return;
            }
            if (string.IsNullOrEmpty(productVersion))
            {
                productVersion = "";
            }
            this.UniqueUserAgents.Add(new ProductInfoHeaderValue(productName, productVersion));
        }

        public void AddUserAgent(string productName)
        {
            this.AddUserAgent(productName, "");
        }

        public HashSet<ProductInfoHeaderValue> UniqueUserAgents { get; set; }

        public ProductInfoHeaderValue[] UserAgents
        {
            get
            {
                return UniqueUserAgents?.ToArray();
            }
        }

        /// <summary>
        /// This class exists to allow adding an additional reference to the httpClient to prevent the client 
        /// from being disposed.  Should not be used except in this mocked context.
        /// </summary>
        class PassThroughDelegatingHandler : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return base.SendAsync(request, cancellationToken);
            }
        }

        public TClient CreateArmClient<TClient>(IAzureContext context, string endpoint) where TClient : Rest.ServiceClient<TClient>
        {
            Debug.Assert(context != null);
            var credentials = AzureSession.Instance.AuthenticationFactory.GetServiceClientCredentials(context);
            var client = CreateCustomArmClient<TClient>(credentials, context.Environment.GetEndpointAsUri(endpoint),
                context.Subscription.Id);
            return client;

        }

        public TClient CreateCustomArmClient<TClient>(params object[] parameters) where TClient : Rest.ServiceClient<TClient>
        {
            TClient client = ManagementClients.FirstOrDefault(o => o is TClient) as TClient;
            if (client == null)
            {
                if (throwWhenNotAvailable)
                {
                    throw new ArgumentException(
                        string.Format("TestManagementClientHelper class wasn't initialized with the {0} client.",
                            typeof(TClient).Name));
                }
                else
                {
                    var realClientFactory = new ClientFactory();
                    var newParameters = new object[parameters.Length + 1];
                    Array.Copy(parameters, 0, newParameters, 1, parameters.Length);
                    newParameters[0] = HttpMockServer.CreateInstance();
                    var realClient = realClientFactory.CreateCustomArmClient<TClient>(newParameters);
                    return realClient;
                }
            }

            if (TestMockSupport.RunningMocked && HttpMockServer.GetCurrentMode() != HttpRecorderMode.Record)
            {
                IAzureClient azureClient = client as IAzureClient;
                if (azureClient != null)
                {
                    azureClient.LongRunningOperationRetryTimeout = 0;
                }
            }

            return client;
        }

        public void RemoveUserAgent(string name)
        {
            UniqueUserAgents.RemoveWhere((p) => string.Equals(p.Product.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
