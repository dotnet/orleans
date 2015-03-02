/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{

    [Serializable]
    internal class ImplicitStreamSubscriberTable
    {
        private readonly Dictionary<string, HashSet<int>> table;

        internal ImplicitStreamSubscriberTable()
        {
            table = new Dictionary<string, HashSet<int>>();
        }

        /// <summary>
        /// Initializes any implicit stream subscriptions specified for a grain class type. If the grain class specified does not have any associated namespaces, then nothing is done.
        /// </summary>
        /// <param name="grainClass">A grain class type.</param>
        /// <exception cref="System.ArgumentException">
        /// Duplicate specification of namespace "...".
        /// </exception>
        internal void InitImplicitStreamSubscribers(IEnumerable<Type> grainClasses)
        {
            foreach (var grainClass in grainClasses)
            {
                if (!TypeUtils.IsGrainClass(grainClass))
                {
                    continue;
                }

                // we collect all namespaces that the specified grain class should implicitly subscribe to.
                ISet<string> namespaces = GetNamespacesFromAttributes(grainClass);
                if (null == namespaces) continue;

                if (namespaces.Count > 0)
                {
                    // the grain class is subscribed to at least one namespace. in order to create a grain reference later, we need a qualifying interface but it doesn't matter which (because we'll be creating references to extensions), so we'll take the first interface in the sequence.
                    AddImplicitSubscriber(grainClass, namespaces);
                }
            }
        }

        /// <summary>
        /// Retrieve a list of implicit subscribers, given a stream ID. This method throws an exception if there's no namespace associated with the stream ID. 
        /// </summary>
        /// <param name="streamId">A stream ID.</param>
        /// <returns>A set of references to implicitly subscribed grains. They are expected to support the streaming consumer extension.</returns>
        /// <exception cref="System.ArgumentException">The stream ID doesn't have an associated namespace.</exception>
        /// <exception cref="System.InvalidOperationException">Internal invariant violation.</exception>
        internal ISet<IStreamConsumerExtension> GetImplicitSubscribers(StreamId streamId)
        {
            if (String.IsNullOrWhiteSpace(streamId.Namespace))
            {
                throw new ArgumentException("The stream ID doesn't have an associated namespace.", "streamId");
            }

            HashSet<int> entry;
            var result = new HashSet<IStreamConsumerExtension>();
            if (table.TryGetValue(streamId.Namespace, out entry))
            {
                foreach (var i in entry)
                {
                    IStreamConsumerExtension consumer = MakeConsumerReference(streamId.Guid, i);
                    if (!result.Add(consumer))
                    {
                        throw new InvalidOperationException(string.Format("Internal invariant violation: generated duplicate subscriber reference: {0}", consumer));
                    }
                }                
                return result;                
            }

            return result;
        }

        /// <summary>
        /// Determines whether the specified grain is an implicit subscriber of a given stream.
        /// </summary>
        /// <param name="grainId">The grain identifier.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>true if the grain id describes an implicit subscriber of the stream described by the stream id.</returns>
        internal bool IsImplicitSubscriber(GrainId grainId, StreamId streamId)
        {
            if (String.IsNullOrWhiteSpace(streamId.Namespace))
            {
                return false;
            }

            HashSet<int> entry;
            return table.TryGetValue(streamId.Namespace, out entry) && entry.Contains(grainId.GetTypeCode());
        }

        /// <summary>
        /// Add an implicit subscriber to the table.
        /// </summary>
        /// <param name="grainClass">Type of the grain class whose instances subscribe to the specified namespaces.</param>
        /// <param name="namespaces">Namespaces instances of the grain class should subscribe to.</param>
        /// <exception cref="System.ArgumentException">
        /// No namespaces specified.
        /// or
        /// Duplicate specification of namespace "...".
        /// </exception>
        private void AddImplicitSubscriber(Type grainClass, ISet<string> namespaces)
        {
            // convert IEnumerable<> to an array without copying, if possible.
            if (namespaces.Count == 0)
            {
                throw new ArgumentException("no namespaces specified", "namespaces");
            }

            // we'll need the class type code.
            int implTypeCode = CodeGeneration.GrainInterfaceData.GetGrainClassTypeCode(grainClass);

            foreach (string s in namespaces)
            {
                // first, we trim whitespace off of the namespace string. leaving these would lead to misleading log messages.
                string key = s.Trim();

                // if the table already holds the namespace we're looking at, then we don't need to create a new entry. each entry is a dictionary that holds associations between class names and interface ids. e.g.:
                //  
                // "namespace0" -> HashSet {implTypeCode.0, implTypeCode.1, ..., implTypeCode.n}
                // 
                // each class in the entry used the ImplicitStreamSubscriptionAtrribute with the associated namespace. this information will be used later to create grain references on-demand. we must use string representations to ensure that this information is serializable.
                if (table.ContainsKey(key))
                {
                    // an entry already exists. we append a class/interface association to the current set.
                    HashSet<int> entries = table[key];
                    if (!entries.Add(implTypeCode))
                    {
                        throw new InvalidOperationException(String.Format("attempt to initialize implicit subscriber more than once (key={0}, implTypeCode={1}).", key, implTypeCode));
                    }
                }
                else
                {
                    // an entry does not already exist. we create a new one with one class/interface association.
                    table[key] = new HashSet<int> { implTypeCode };
                }
            }
        }

        /// <summary>
        /// Create a reference to a grain that we expect to support the stream consumer extension.
        /// </summary>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassName">The name of the grain class to instantiate.</param>
        /// <param name="grainIfaceName">The name of the an IGrain-derived interface that `className` implements (required by MakeGrainReferenceInternal)</param>
        /// <returns></returns>
        private IStreamConsumerExtension MakeConsumerReference(Guid primaryKey, int implTypeCode)
        {
            GrainId grainId = GrainId.GetGrainId(implTypeCode, primaryKey);
            IAddressable addressable = GrainReference.FromGrainId(grainId);
            return GrainFactory.Cast<IStreamConsumerExtension>(addressable);
        }

        /// <summary>
        /// Collects the namespaces associated with a grain class type through the use of ImplicitStreamSubscriptionAttribute.
        /// </summary>
        /// <param name="grainClass">A grain class type that might have ImplicitStreamSubscriptionAttributes associated with it.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentException">grainType does not describe a grain class.</exception>
        /// <exception cref="System.InvalidOperationException">duplicate specification of ImplicitConsumerActivationAttribute(...).</exception>
        private static ISet<string> GetNamespacesFromAttributes(Type grainClass)
        {
            if (!TypeUtils.IsGrainClass(grainClass))
            {
                throw new ArgumentException(string.Format("{0} is not a grain class.", grainClass.FullName), "grainClass");
            }

            object[] attribs = grainClass.GetCustomAttributes(typeof(ImplicitStreamSubscriptionAttribute), inherit: false);

            // otherwise, we'll consider all of them and aggregate the specifications. duplicates will not be permitted.
            var result = new HashSet<string>();
            foreach (var ob in attribs)
            {
                var attrib = (ImplicitStreamSubscriptionAttribute)ob;
                if (string.IsNullOrWhiteSpace(attrib.Namespace))
                {
                    throw new InvalidOperationException("ImplicitConsumerActivationAttribute argument cannot be null nor whitespace");
                }

                string trimmed = attrib.Namespace;
                if (!result.Add(trimmed))
                {
                    throw new InvalidOperationException(string.Format("duplicate specification of attribute ImplicitConsumerActivationAttribute({0}).", attrib.Namespace));
                }
            }

            return result;
        }
    }
}