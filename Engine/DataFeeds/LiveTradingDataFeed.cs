/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using QuantConnect.Configuration;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Data.Custom.Tiingo;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Provides an implementation of <see cref="IDataFeed"/> that is designed to deal with
    /// live, remote data sources
    /// </summary>
    public class LiveTradingDataFeed : FileSystemDataFeed
    {
        private static readonly int MaximumWarmupHistoryDaysLookBack = Config.GetInt("maximum-warmup-history-days-look-back", 7);

        private LiveNodePacket _job;

        // used to get current time
        private ITimeProvider _timeProvider;
        private IAlgorithm _algorithm;
        private ITimeProvider _frontierTimeProvider;
        private IDataProvider _dataProvider;
        private IMapFileProvider _mapFileProvider;
        private IDataQueueHandler _dataQueueHandler;
        private BaseDataExchange _customExchange;
        private SubscriptionCollection _subscriptions;
        private IFactorFileProvider _factorFileProvider;
        private IDataChannelProvider _channelProvider;

        /// <summary>
        /// Public flag indicator that the thread is still busy.
        /// </summary>
        public bool IsActive
        {
            get; private set;
        }

        /// <summary>
        /// Initializes the data feed for the specified job and algorithm
        /// </summary>
        public override void Initialize(IAlgorithm algorithm,
            AlgorithmNodePacket job,
            IResultHandler resultHandler,
            IMapFileProvider mapFileProvider,
            IFactorFileProvider factorFileProvider,
            IDataProvider dataProvider,
            IDataFeedSubscriptionManager subscriptionManager,
            IDataFeedTimeProvider dataFeedTimeProvider,
            IDataChannelProvider dataChannelProvider)
        {
            if (!(job is LiveNodePacket))
            {
                throw new ArgumentException("The LiveTradingDataFeed requires a LiveNodePacket.");
            }

            _algorithm = algorithm;
            _job = (LiveNodePacket)job;
            _timeProvider = dataFeedTimeProvider.TimeProvider;
            _dataProvider = dataProvider;
            _mapFileProvider = mapFileProvider;
            _factorFileProvider = factorFileProvider;
            _channelProvider = dataChannelProvider;
            _frontierTimeProvider = dataFeedTimeProvider.FrontierTimeProvider;
            _customExchange = GetBaseDataExchange();
            _subscriptions = subscriptionManager.DataFeedSubscriptions;

            _dataQueueHandler = GetDataQueueHandler();
            _dataQueueHandler?.SetJob(_job);

            // run the custom data exchange
            _customExchange.Start();

            IsActive = true;

            base.Initialize(algorithm, job, resultHandler, mapFileProvider, factorFileProvider, dataProvider, subscriptionManager, dataFeedTimeProvider, dataChannelProvider);
        }

        /// <summary>
        /// Creates a new subscription to provide data for the specified security.
        /// </summary>
        /// <param name="request">Defines the subscription to be added, including start/end times the universe and security</param>
        /// <returns>The created <see cref="Subscription"/> if successful, null otherwise</returns>
        public override Subscription CreateSubscription(SubscriptionRequest request)
        {
            // create and add the subscription to our collection
            var subscription = request.IsUniverseSubscription
                ? CreateUniverseSubscription(request)
                : CreateDataSubscription(request);

            return subscription;
        }

        /// <summary>
        /// Removes the subscription from the data feed, if it exists
        /// </summary>
        /// <param name="subscription">The subscription to remove</param>
        public override void RemoveSubscription(Subscription subscription)
        {
            var symbol = subscription.Configuration.Symbol;

            // remove the subscriptions
            if (!_channelProvider.ShouldStreamSubscription(subscription.Configuration))
            {
                _customExchange.RemoveEnumerator(symbol);
            }
            else
            {
                _dataQueueHandler.UnsubscribeWithMapping(subscription.Configuration);
                if (subscription.Configuration.SecurityType == SecurityType.Equity && !subscription.Configuration.IsInternalFeed)
                {
                    _dataQueueHandler.UnsubscribeWithMapping(new SubscriptionDataConfig(subscription.Configuration, typeof(Dividend)));
                    _dataQueueHandler.UnsubscribeWithMapping(new SubscriptionDataConfig(subscription.Configuration, typeof(Split)));
                }
            }
        }

        /// <summary>
        /// External controller calls to signal a terminate of the thread.
        /// </summary>
        public override void Exit()
        {
            if (IsActive)
            {
                IsActive = false;
                Log.Trace("LiveTradingDataFeed.Exit(): Start. Setting cancellation token...");
                _customExchange?.Stop();
                Log.Trace("LiveTradingDataFeed.Exit(): Exit Finished.");

                base.Exit();
            }
        }

        /// <summary>
        /// Gets the <see cref="IDataQueueHandler"/> to use by default <see cref="DataQueueHandlerManager"/>
        /// </summary>
        /// <remarks>Useful for testing</remarks>
        /// <returns>The loaded <see cref="IDataQueueHandler"/></returns>
        protected virtual IDataQueueHandler GetDataQueueHandler()
        {
            return new DataQueueHandlerManager();
        }

        /// <summary>
        /// Gets the <see cref="BaseDataExchange"/> to use
        /// </summary>
        /// <remarks>Useful for testing</remarks>
        protected virtual BaseDataExchange GetBaseDataExchange()
        {
            return new BaseDataExchange("CustomDataExchange") { SleepInterval = 100 };
        }

        /// <summary>
        /// Creates a new subscription for the specified security
        /// </summary>
        /// <param name="request">The subscription request</param>
        /// <returns>A new subscription instance of the specified security</returns>
        private Subscription CreateDataSubscription(SubscriptionRequest request)
        {
            Subscription subscription = null;

            try
            {
                var localEndTime = request.EndTimeUtc.ConvertFromUtc(request.Security.Exchange.TimeZone);
                var timeZoneOffsetProvider = new TimeZoneOffsetProvider(request.Configuration.ExchangeTimeZone, request.StartTimeUtc, request.EndTimeUtc);

                IEnumerator<BaseData> enumerator = null;
                // during warmup we might get requested to add some asset which has already expired in which case the live enumerator will be empty
                if (!IsExpired(request.Configuration))
                {
                    if (!_channelProvider.ShouldStreamSubscription(request.Configuration))
                    {
                        if (!Tiingo.IsAuthCodeSet)
                        {
                            // we're not using the SubscriptionDataReader, so be sure to set the auth token here
                            Tiingo.SetAuthCode(Config.Get("tiingo-auth-token"));
                        }

                        var factory = new LiveCustomDataSubscriptionEnumeratorFactory(_timeProvider);
                        var enumeratorStack = factory.CreateEnumerator(request, _dataProvider);

                        var enqueable = new EnqueueableEnumerator<BaseData>();
                        _customExchange.AddEnumerator(request.Configuration.Symbol, enumeratorStack, handleData: data =>
                        {
                            enqueable.Enqueue(data);

                            subscription.OnNewDataAvailable();
                        });

                        enumerator = enqueable;
                    }
                    else
                    {
                        var auxEnumerators = new List<IEnumerator<BaseData>>();

                        if (LiveAuxiliaryDataEnumerator.TryCreate(request.Configuration, _timeProvider, _dataQueueHandler,
                            request.Security.Cache, _mapFileProvider, _factorFileProvider, request.StartTimeLocal, out var auxDataEnumator))
                        {
                            auxEnumerators.Add(auxDataEnumator);
                        }

                        EventHandler handler = (_, _) => subscription?.OnNewDataAvailable();
                        enumerator = Subscribe(request.Configuration, handler);

                        if (request.Configuration.EmitSplitsAndDividends())
                        {
                            auxEnumerators.Add(Subscribe(new SubscriptionDataConfig(request.Configuration, typeof(Dividend)), handler));
                            auxEnumerators.Add(Subscribe(new SubscriptionDataConfig(request.Configuration, typeof(Split)), handler));
                        }

                        if (auxEnumerators.Count > 0)
                        {
                            enumerator = new LiveAuxiliaryDataSynchronizingEnumerator(_timeProvider, request.Configuration.ExchangeTimeZone, enumerator, auxEnumerators);
                        }
                    }

                    // scale prices before 'SubscriptionFilterEnumerator' since it updates securities realtime price
                    // and before fill forwarding so we don't happen to apply twice the factor
                    if (request.Configuration.PricesShouldBeScaled(liveMode: true))
                    {
                        enumerator = new PriceScaleFactorEnumerator(
                            enumerator,
                            request.Configuration,
                            _factorFileProvider,
                            liveMode: true);
                    }

                    if (request.Configuration.FillDataForward)
                    {
                        var fillForwardResolution = _subscriptions.UpdateAndGetFillForwardResolution(request.Configuration);

                        enumerator = new LiveFillForwardEnumerator(_frontierTimeProvider, enumerator, request.Security.Exchange, fillForwardResolution, request.Configuration.ExtendedMarketHours, localEndTime, request.Configuration.Increment, request.Configuration.DataTimeZone);
                    }

                    // define market hours and user filters to incoming data
                    if (request.Configuration.IsFilteredSubscription)
                    {
                        enumerator = new SubscriptionFilterEnumerator(enumerator, request.Security, localEndTime, request.Configuration.ExtendedMarketHours, true, request.ExchangeHours);
                    }

                    // finally, make our subscriptions aware of the frontier of the data feed, prevents future data from spewing into the feed
                    enumerator = new FrontierAwareEnumerator(enumerator, _frontierTimeProvider, timeZoneOffsetProvider);
                }
                else
                {
                    enumerator = Enumerable.Empty<BaseData>().GetEnumerator();
                }

                enumerator = GetWarmupEnumerator(request, enumerator);

                var subscriptionDataEnumerator = new SubscriptionDataEnumerator(request.Configuration, request.Security.Exchange.Hours, timeZoneOffsetProvider, enumerator, request.IsUniverseSubscription);
                subscription = new Subscription(request, subscriptionDataEnumerator, timeZoneOffsetProvider);
            }
            catch (Exception err)
            {
                Log.Error(err);
            }

            return subscription;
        }

        /// <summary>
        /// Helper method to determine if the symbol associated with the requested configuration is expired or not
        /// </summary>
        /// <remarks>This is useful during warmup where we can be requested to add some already expired asset. We want to skip sending it
        /// to our live <see cref="_dataQueueHandler"/> instance to avoid explosions. But we do want to add warmup enumerators</remarks>
        private bool IsExpired(SubscriptionDataConfig dataConfig)
        {
            var mapFile = _mapFileProvider.ResolveMapFile(dataConfig);
            var delistingDate = dataConfig.Symbol.GetDelistingDate(mapFile);
            return _timeProvider.GetUtcNow().Date > delistingDate.ConvertToUtc(dataConfig.ExchangeTimeZone);
        }

        private IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            return new LiveSubscriptionEnumerator(dataConfig, _dataQueueHandler, newDataAvailableHandler);
        }

        /// <summary>
        /// Creates a new subscription for universe selection
        /// </summary>
        /// <param name="request">The subscription request</param>
        private Subscription CreateUniverseSubscription(SubscriptionRequest request)
        {
            Subscription subscription = null;

            // TODO : Consider moving the creating of universe subscriptions to a separate, testable class

            // grab the relevant exchange hours
            var config = request.Universe.Configuration;
            var localEndTime = request.EndTimeUtc.ConvertFromUtc(request.Security.Exchange.TimeZone);
            var tzOffsetProvider = new TimeZoneOffsetProvider(request.Configuration.ExchangeTimeZone, request.StartTimeUtc, request.EndTimeUtc);

            IEnumerator<BaseData> enumerator = null;

            var timeTriggered = request.Universe as ITimeTriggeredUniverse;
            if (timeTriggered != null)
            {
                Log.Trace($"LiveTradingDataFeed.CreateUniverseSubscription(): Creating user defined universe: {config.Symbol.ID}");

                // spoof a tick on the requested interval to trigger the universe selection function
                var enumeratorFactory = new TimeTriggeredUniverseSubscriptionEnumeratorFactory(timeTriggered, MarketHoursDatabase.FromDataFolder(), _frontierTimeProvider);
                enumerator = enumeratorFactory.CreateEnumerator(request, _dataProvider);

                enumerator = new FrontierAwareEnumerator(enumerator, _timeProvider, tzOffsetProvider);

                var enqueueable = new EnqueueableEnumerator<BaseData>();
                _customExchange.AddEnumerator(new EnumeratorHandler(config.Symbol, enumerator, enqueueable));
                enumerator = enqueueable;
            }
            else if (config.Type == typeof(CoarseFundamental) || config.Type == typeof(ETFConstituentData))
            {
                Log.Trace($"LiveTradingDataFeed.CreateUniverseSubscription(): Creating {config.Type.Name} universe: {config.Symbol.ID}");

                // Will try to pull data from the data folder every 10min, file with yesterdays date.
                // If lean is started today it will trigger initial coarse universe selection
                var factory = new LiveCustomDataSubscriptionEnumeratorFactory(_timeProvider,
                    // we adjust time to the previous tradable date
                    time => Time.GetStartTimeForTradeBars(request.Security.Exchange.Hours, time, Time.OneDay, 1, false, config.DataTimeZone),
                    TimeSpan.FromMinutes(10)
                );
                var enumeratorStack = factory.CreateEnumerator(request, _dataProvider);

                // aggregates each coarse data point into a single BaseDataCollection
                var aggregator = new BaseDataCollectionAggregatorEnumerator(enumeratorStack, config.Symbol, true);
                var enqueable = new EnqueueableEnumerator<BaseData>();
                _customExchange.AddEnumerator(config.Symbol, aggregator, handleData: data =>
                {
                    enqueable.Enqueue(data);
                    subscription.OnNewDataAvailable();
                });

                enumerator = GetConfiguredFrontierAwareEnumerator(enqueable, tzOffsetProvider,
                    // advance time if before 23pm or after 5am and not on Saturdays
                    time => time.Hour < 23 && time.Hour > 5 && time.DayOfWeek != DayOfWeek.Saturday);
            }
            else if (request.Universe is OptionChainUniverse)
            {
                Log.Trace("LiveTradingDataFeed.CreateUniverseSubscription(): Creating option chain universe: " + config.Symbol.ID);

                Func<SubscriptionRequest, IEnumerator<BaseData>> configure = (subRequest) =>
                {
                    var fillForwardResolution = _subscriptions.UpdateAndGetFillForwardResolution(subRequest.Configuration);
                    var input = Subscribe(subRequest.Configuration, (sender, args) => subscription.OnNewDataAvailable());
                    return new LiveFillForwardEnumerator(_frontierTimeProvider, input, subRequest.Security.Exchange, fillForwardResolution, subRequest.Configuration.ExtendedMarketHours, localEndTime, subRequest.Configuration.Increment, subRequest.Configuration.DataTimeZone);
                };

                var symbolUniverse = GetUniverseProvider(SecurityType.Option);

                var enumeratorFactory = new OptionChainUniverseSubscriptionEnumeratorFactory(configure, symbolUniverse, _timeProvider);
                enumerator = enumeratorFactory.CreateEnumerator(request, _dataProvider);

                enumerator = new FrontierAwareEnumerator(enumerator, _frontierTimeProvider, tzOffsetProvider);
            }
            else if (request.Universe is FuturesChainUniverse)
            {
                Log.Trace("LiveTradingDataFeed.CreateUniverseSubscription(): Creating futures chain universe: " + config.Symbol.ID);

                var symbolUniverse = GetUniverseProvider(SecurityType.Option);

                var enumeratorFactory = new FuturesChainUniverseSubscriptionEnumeratorFactory(symbolUniverse, _timeProvider);
                enumerator = enumeratorFactory.CreateEnumerator(request, _dataProvider);

                enumerator = new FrontierAwareEnumerator(enumerator, _frontierTimeProvider, tzOffsetProvider);
            }
            else
            {
                Log.Trace("LiveTradingDataFeed.CreateUniverseSubscription(): Creating custom universe: " + config.Symbol.ID);

                var factory = new LiveCustomDataSubscriptionEnumeratorFactory(_timeProvider);
                var enumeratorStack = factory.CreateEnumerator(request, _dataProvider);
                enumerator = new BaseDataCollectionAggregatorEnumerator(enumeratorStack, config.Symbol, liveMode: true);

                var enqueueable = new EnqueueableEnumerator<BaseData>();
                _customExchange.AddEnumerator(new EnumeratorHandler(config.Symbol, enumerator, enqueueable));
                enumerator = enqueueable;
            }

            enumerator = GetWarmupEnumerator(request, enumerator);

            // create the subscription
            var subscriptionDataEnumerator = new SubscriptionDataEnumerator(request.Configuration, request.Security.Exchange.Hours, tzOffsetProvider, enumerator, request.IsUniverseSubscription);
            subscription = new Subscription(request, subscriptionDataEnumerator, tzOffsetProvider);

            // send the subscription for the new symbol through to the data queuehandler
            if (_channelProvider.ShouldStreamSubscription(subscription.Configuration))
            {
                Subscribe(request.Configuration, (sender, args) => subscription.OnNewDataAvailable());
            }

            return subscription;
        }

        /// <summary>
        /// Build and apply the warmup enumerators when required
        /// </summary>
        private IEnumerator<BaseData> GetWarmupEnumerator(SubscriptionRequest request, IEnumerator<BaseData> liveEnumerator)
        {
            if (_algorithm.IsWarmingUp)
            {
                var warmup = new SubscriptionRequest(request, endTimeUtc: _timeProvider.GetUtcNow());
                if (warmup.TradableDays.Any())
                {
                    // since we will source data locally and from the history provider, let's limit the history request size
                    // by setting a start date respecting the 'MaximumWarmupHistoryDaysLookBack'
                    var historyWarmup = warmup;
                    var warmupHistoryStartDate = warmup.EndTimeUtc.AddDays(-MaximumWarmupHistoryDaysLookBack);
                    if (warmupHistoryStartDate > warmup.StartTimeUtc)
                    {
                        historyWarmup = new SubscriptionRequest(warmup, startTimeUtc: warmupHistoryStartDate);
                    }

                    // the order here is important, concat enumerator will keep the last enumerator given and dispose of the rest
                    liveEnumerator = new ConcatEnumerator(true, GetFileBasedWarmupEnumerator(warmup),
                        GetHistoryWarmupEnumerator(historyWarmup), liveEnumerator);
                }
            }
            return liveEnumerator;
        }

        /// <summary>
        /// File based warmup enumerator
        /// </summary>
        private IEnumerator<BaseData> GetFileBasedWarmupEnumerator(SubscriptionRequest warmup)
        {
            IEnumerator<BaseData> result = null;
            try
            {
                result = new FilterEnumerator<BaseData>(CreateEnumerator(warmup),
                    // don't let future data past, nor fill forward, that will be handled by the history request
                    data => data == null || data.EndTime < warmup.EndTimeLocal && !data.IsFillForward);
            }
            catch (Exception e)
            {
                Log.Error(e, $"File based warmup: {warmup.Configuration}");
            }
            return result;
        }

        /// <summary>
        /// History based warmup enumerator
        /// </summary>
        private IEnumerator<BaseData> GetHistoryWarmupEnumerator(SubscriptionRequest warmup)
        {
            IEnumerator<BaseData> result = null;
            try
            {
                var historyRequest = new Data.HistoryRequest(warmup.Configuration, warmup.ExchangeHours, warmup.StartTimeUtc, warmup.EndTimeUtc);
                result = _algorithm.HistoryProvider.GetHistory(new[] { historyRequest }, _algorithm.TimeZone).Select(slice =>
                {
                    try
                    {
                        var data = slice.Get(historyRequest.DataType);
                        return (BaseData)data[warmup.Configuration.Symbol];
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, $"History warmup: {warmup.Configuration}");
                    }
                    return null;
                }).GetEnumerator();

                result = new FilterEnumerator<BaseData>(result, data => data == null || data.EndTime < warmup.EndTimeLocal);
            }
            catch
            {
                // some history providers could throw if they do not support a type
            }
            return result;
        }

        /// <summary>
        /// Will wrap the provided enumerator with a <see cref="FrontierAwareEnumerator"/>
        /// using a <see cref="PredicateTimeProvider"/> that will advance time based on the provided
        /// function
        /// </summary>
        /// <remarks>Won't advance time if now.Hour is bigger or equal than 23pm, less or equal than 5am or Saturday.
        /// This is done to prevent universe selection occurring in those hours so that the subscription changes
        /// are handled correctly.</remarks>
        private IEnumerator<BaseData> GetConfiguredFrontierAwareEnumerator(
            IEnumerator<BaseData> enumerator,
            TimeZoneOffsetProvider tzOffsetProvider,
            Func<DateTime, bool> customStepEvaluator)
        {
            var stepTimeProvider = new PredicateTimeProvider(_frontierTimeProvider, customStepEvaluator);

            return new FrontierAwareEnumerator(enumerator, stepTimeProvider, tzOffsetProvider);
        }

        private IDataQueueUniverseProvider GetUniverseProvider(SecurityType securityType)
        {
            if (_dataQueueHandler is not IDataQueueUniverseProvider or DataQueueHandlerManager { HasUniverseProvider: false })
            {
                throw new NotSupportedException($"The DataQueueHandler does not support {securityType}.");
            }
            return (IDataQueueUniverseProvider)_dataQueueHandler;
        }

        /// <summary>
        /// Overrides methods of the base data exchange implementation
        /// </summary>
        private class EnumeratorHandler : BaseDataExchange.EnumeratorHandler
        {
            public EnumeratorHandler(Symbol symbol, IEnumerator<BaseData> enumerator, EnqueueableEnumerator<BaseData> enqueueable)
                : base(symbol, enumerator, handleData: enqueueable.Enqueue)
            {
                EnumeratorFinished += (_, _) => enqueueable.Stop();
            }
        }
    }
}
