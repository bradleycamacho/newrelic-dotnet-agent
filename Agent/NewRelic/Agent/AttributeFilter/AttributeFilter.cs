﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using NewRelic.Trie;

namespace NewRelic.Agent
{
	public class AttributeFilter<T> : IAttributeFilter<T> where T : IAttribute
	{
		private const uint MaxCacheSize = 1000;

		private readonly TrieNode<AttributeFilterNode> _explicitAttributeTrie;

		private readonly TrieNode<AttributeFilterNode> _implicitAttributeTrie;

		private readonly Settings _settings;

		private readonly ConcurrentDictionary<string, bool> _cachedClusions = new ConcurrentDictionary<string, bool>();

		public AttributeFilter(Settings settings)
		{
			_settings = settings;
			var explicitAttributeNodes = CreateExplicitAttributeNodes(settings);
			var implicitAttributeNodes = CreateImplicitAttributeNodes(settings);
			_explicitAttributeTrie = CreateAttributeNodeTrie(explicitAttributeNodes);
			_implicitAttributeTrie = CreateAttributeNodeTrie(implicitAttributeNodes);
		}

		public IEnumerable<T> FilterAttributes(IEnumerable<T> attributes, AttributeDestinations destination)
		{
			if (!_settings.AttributesEnabled)
				return Enumerable.Empty<T>();

			switch (destination)
			{
				case AttributeDestinations.ErrorTrace:
					if (!_settings.ErrorTraceEnabled)
						return Enumerable.Empty<T>();
					break;
				case AttributeDestinations.JavaScriptAgent:
					if (!_settings.JavaScriptAgentEnabled)
						return Enumerable.Empty<T>();
					break;
				case AttributeDestinations.TransactionEvent:
					if (!_settings.TransactionEventEnabled)
						return Enumerable.Empty<T>();
					break;
				case AttributeDestinations.TransactionTrace:
					if (!_settings.TransactionTraceEnabled)
						return Enumerable.Empty<T>();
					break;
				case AttributeDestinations.ErrorEvent:
					if (!_settings.ErrorEventsEnabled)
						return Enumerable.Empty<T>();
					break;
				case AttributeDestinations.SqlTrace:
					break;
				default:
					throw new ArgumentOutOfRangeException("destination", "Unexpected destination: " + destination);
			}

			
			var filteredAttributes = new List<T>();
			foreach (var attr in attributes)
			{
				if (ShouldIncludeAttribute(attr, destination))
				{
					filteredAttributes.Add(attr);
				}
			}

			return filteredAttributes;
		}

		
		private bool ShouldIncludeAttribute(T attribute, AttributeDestinations destination)
		{
			var cachedClusion = CheckAttributeClusionCache(attribute, destination);
			if (cachedClusion != null)
				return cachedClusion.Value;

			var result = !ShouldExcludeAttribute(attribute, destination);

			AddToAttributeClusionCache(attribute, destination, result);

			return result;
		}
		

		private bool? CheckAttributeClusionCache(T attribute, AttributeDestinations destination)
		{
			var cacheKey = GetAttributeClusionKey(attribute, destination);
			if (_cachedClusions.TryGetValue(cacheKey, out bool cachedClusion))
			{
				return cachedClusion;
			}

			return null;
		}

		private void AddToAttributeClusionCache(T attribute, AttributeDestinations destination, bool result)
		{
			if (_cachedClusions.Count > MaxCacheSize)
				return;

			var cacheKey = GetAttributeClusionKey(attribute, destination);
			_cachedClusions[cacheKey] = result;
		}
		
		private static string GetAttributeClusionKey(T attribute, AttributeDestinations destination)
		{
			// Enum is cast to byte to avoid enum.ToString which does reflection 
			// The cache key includes both the intended destinations of the attribute (attribute.DefaultDestinations) 
			// and the destination being tested.  This is because some attributes have the same name (like timestamp)
			// but different destinations.  Without this cache key, the wrong value will be selected.
			return attribute.Key + (((byte)destination << 8) + (byte)attribute.DefaultDestinations).ToString();
		}

		private bool ShouldExcludeAttribute(T attribute, AttributeDestinations destination)
		{
			var explicitClusion = CheckForExplicitClusion(attribute, destination);
			if (explicitClusion != Clude.Unknown)
				return KnownCludeToBoolean(explicitClusion);

			var implicitClusion = CheckForImplicitClusion(attribute, destination);
			if (implicitClusion != Clude.Unknown)
				return KnownCludeToBoolean(implicitClusion);

			return false;
		}

		private Clude CheckForExplicitClusion(T attribute, AttributeDestinations destination)
		{
			return _explicitAttributeTrie.GetClusion(attribute, destination);
		}

		private Clude CheckForImplicitClusion(T attribute, AttributeDestinations destination)
		{
			if ((attribute.DefaultDestinations & destination) != destination)
				return Clude.Exclude;

			return _implicitAttributeTrie.GetClusion(attribute, destination);
		}

		private static bool KnownCludeToBoolean(Clude clude)
		{
			switch (clude)
			{
				case Clude.Exclude:
					return true;
				case Clude.Include:
					return false;
				case Clude.Unknown:
					throw new Exception("Expected exclude or include but found Unknown.");
				default:
					throw new Exception("Expected exclude or include but found " + clude);
			}
		}

		private static TrieNode<AttributeFilterNode> CreateAttributeNodeTrie(IEnumerable<AttributeFilterNode> nodes)
		{
			var trieBuilder = new TrieBuilder<AttributeFilterNode>(
				rootNodeDataFactory: () => new AttributeFilterNode("*", AttributeDestinations.None, AttributeDestinations.None),
				nodeDataMerger: MergeAttributeNodes,
				nodeDataComparor: CompareAttributeNodes,
				nodeDataHasher: HashAttributeNode,
				canParentAcceptChildChecker: CanParentAcceptChild,
				canNodeHaveChildrenChecker: CanNodeHaveChildren);

			return trieBuilder.CreateTrie(nodes);
		}

		private static IEnumerable<AttributeFilterNode> CreateExplicitAttributeNodes(Settings settings)
		{
			var globalIncludes = CreateAttributeNodes(settings.Includes, AttributeDestinations.All, true);
			var globalExcludes = CreateAttributeNodes(settings.Excludes, AttributeDestinations.All, false);
			var errorTraceIncludes = CreateAttributeNodes(settings.ErrorTraceIncludes, AttributeDestinations.ErrorTrace, true);
			var errorTraceExcludes = CreateAttributeNodes(settings.ErrorTraceExcludes, AttributeDestinations.ErrorTrace, false);
			var javaScriptAgentIncludes = CreateAttributeNodes(settings.JavaScriptAgentIncludes, AttributeDestinations.JavaScriptAgent, true);
			var javaScriptAgentExcludes = CreateAttributeNodes(settings.JavaScriptAgentExcludes, AttributeDestinations.JavaScriptAgent, false);
			var transactionEventIncludes = CreateAttributeNodes(settings.TransactionEventIncludes, AttributeDestinations.TransactionEvent, true);
			var transactionEventExcludes = CreateAttributeNodes(settings.TransactionEventExcludes, AttributeDestinations.TransactionEvent, false);
			var transactionTraceIncludes = CreateAttributeNodes(settings.TransactionTraceIncludes, AttributeDestinations.TransactionTrace, true);
			var transactionTraceExcludes = CreateAttributeNodes(settings.TransactionTraceExcludes, AttributeDestinations.TransactionTrace, false);
			var eventErrorIncludes = CreateAttributeNodes(settings.ErrorEventIncludes, AttributeDestinations.ErrorEvent, true);
			var eventErrorExcludes = CreateAttributeNodes(settings.ErrorEventExcludes, AttributeDestinations.ErrorEvent, false);

			return globalIncludes
				.Concat(globalExcludes)
				.Concat(errorTraceIncludes)
				.Concat(errorTraceExcludes)
				.Concat(javaScriptAgentIncludes)
				.Concat(javaScriptAgentExcludes)
				.Concat(transactionEventIncludes)
				.Concat(transactionEventExcludes)
				.Concat(transactionTraceIncludes)
				.Concat(transactionTraceExcludes)
				.Concat(eventErrorIncludes)
				.Concat(eventErrorExcludes);
		}

		private static IEnumerable<AttributeFilterNode> CreateImplicitAttributeNodes(Settings settings)
		{
			var globalIncludes = CreateAttributeNodes(settings.ImplicitIncludes, AttributeDestinations.All, true);
			var globalExcludes = CreateAttributeNodes(settings.ImplicitExcludes, AttributeDestinations.All, false);

			return globalIncludes.Concat(globalExcludes);
		}

		private static IEnumerable<AttributeFilterNode> CreateAttributeNodes(IEnumerable<string> keys, AttributeDestinations destinations, bool include)
		{
			return keys
				.Where(key => key != null)
				.Select(key => CreateAttributeNode(key, destinations, include));
		}

		private static AttributeFilterNode CreateAttributeNode(string key, AttributeDestinations destinations, bool include)
		{
			var includes = (include) ? destinations : AttributeDestinations.None;
			var excludes = (include) ? AttributeDestinations.None : destinations;
			return new AttributeFilterNode(key, includes, excludes);
		}

		private static AttributeFilterNode MergeAttributeNodes(IEnumerable<AttributeFilterNode> nodeBuilders)
		{
			var mergedNode = nodeBuilders.Aggregate(MergeAttributeNodes);
			if (mergedNode == null)
				throw new NullReferenceException("Attempt to merge attribute nodes yielded a null result.");
			return mergedNode;
		}

		private static AttributeFilterNode MergeAttributeNodes(AttributeFilterNode left, AttributeFilterNode right)
		{
			var key = (left.Wildcard) ? left.Key + "*" : left.Key;
			var includes = left.DestinationIncludes | right.DestinationIncludes;
			var excludes = left.DestinationExcludes | right.DestinationExcludes;
			return new AttributeFilterNode(key, includes, excludes);
		}

		private static int CompareAttributeNodes(AttributeFilterNode left, AttributeFilterNode right)
		{
			// keys are different, just use the key comparison result
			if (left.Key != right.Key)
			{
				return string.Compare(left.Key, right.Key, StringComparison.Ordinal);
			}

			// keys match and wildcard is the same, the attribute nodes are the same (possibly different rules, will need merging)
			if (left.Wildcard == right.Wildcard)
			{
				return 0;
			}

			// keys match, wildcards differ, the one with the wildcard comes first
			return left.Wildcard ? -1 : 1;
		}

		private static int HashAttributeNode(AttributeFilterNode nodeBuilder)
		{
			var suffix = (nodeBuilder.Wildcard) ? "*" : "";
			var stringToHash = nodeBuilder.Key + suffix;
			return stringToHash.GetHashCode();
		}

		private static bool CanParentAcceptChild(AttributeFilterNode parent, AttributeFilterNode orphan)
		{
			if (!parent.Wildcard)
			{
				return false;
			}

			if (!orphan.Key.StartsWith(parent.Key))
			{
				return false;
			}

			return true;
		}

		private static bool CanNodeHaveChildren(AttributeFilterNode node)
		{
			return node.Wildcard;
		}

		public class Settings
		{
			public bool AttributesEnabled = true;
			public IEnumerable<string> Excludes = Enumerable.Empty<string>();
			public IEnumerable<string> Includes = Enumerable.Empty<string>();

			public bool JavaScriptAgentEnabled = true;
			public IEnumerable<string> JavaScriptAgentExcludes = Enumerable.Empty<string>();
			public IEnumerable<string> JavaScriptAgentIncludes = Enumerable.Empty<string>();

			public bool ErrorTraceEnabled = true;
			public IEnumerable<string> ErrorTraceExcludes = Enumerable.Empty<string>();
			public IEnumerable<string> ErrorTraceIncludes = Enumerable.Empty<string>();

			public bool TransactionEventEnabled = true;
			public IEnumerable<string> TransactionEventExcludes = Enumerable.Empty<string>();
			public IEnumerable<string> TransactionEventIncludes = Enumerable.Empty<string>();

			public bool TransactionTraceEnabled = true;
			public IEnumerable<string> TransactionTraceExcludes = Enumerable.Empty<string>();
			public IEnumerable<string> TransactionTraceIncludes = Enumerable.Empty<string>();

			public IEnumerable<string> ImplicitExcludes = Enumerable.Empty<string>();
			public IEnumerable<string> ImplicitIncludes = Enumerable.Empty<string>();

			public IEnumerable<string> ErrorEventExcludes = Enumerable.Empty<string>();
			public IEnumerable<string> ErrorEventIncludes = Enumerable.Empty<string>();
			public bool ErrorEventsEnabled = true;

		}
	}
}
