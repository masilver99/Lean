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
*/

using Newtonsoft.Json;
using QuantConnect.Interfaces;
using System;

namespace QuantConnect.Orders
{
    /// <summary>
    /// Contains additional properties and settings for an order
    /// </summary>
    public class OrderProperties : IOrderProperties
    {
        /// <summary>
        /// Defines the length of time over which an order will continue working before it is cancelled
        /// </summary>
        public TimeInForce TimeInForce { get; set; }

        /// <inheritdoc/>
        public int LinkedOrderId { get; set;}

        /// <summary>
        /// Defines the exchange name for a particular market
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public Exchange Exchange { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrderProperties"/> class
        /// </summary>
        public OrderProperties()
        {
            TimeInForce = TimeInForce.GoodTilCanceled;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrderProperties"/> class, with exchange param
        ///<param name="exchange">Exchange name for market</param>
        /// </summary>
        public OrderProperties(Exchange exchange) : this()
        {
            Exchange = exchange;
        }

        /// <summary>
        /// Returns a new instance clone of this object
        /// </summary>
        public virtual IOrderProperties Clone()
        {
            return (OrderProperties)MemberwiseClone();
        }
    }
}
