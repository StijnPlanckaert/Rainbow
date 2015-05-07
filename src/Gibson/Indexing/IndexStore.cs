﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Sitecore.Diagnostics;
using Sitecore.StringExtensions;

namespace Gibson.Indexing
{
	public class IndexStore : IIndex
	{
		private ConcurrentDictionary<Guid, IndexEntry> _indexById;
		private ConcurrentDictionary<Guid, List<IndexEntry>> _indexByTemplate;
		private ConcurrentDictionary<Guid, List<IndexEntry>> _indexByChildren;
		private ConcurrentDictionary<string, List<IndexEntry>> _indexByPath;
		private bool _isInitialized = false;
		protected readonly object SyncRoot = new object();

		public void Initialize(IList<IndexEntry> entries)
		{
			if (_isInitialized) return;

			lock (SyncRoot)
			{
				if (_isInitialized) return;

				_indexById = new ConcurrentDictionary<Guid, IndexEntry>();
				_indexByTemplate = new ConcurrentDictionary<Guid, List<IndexEntry>>();
				_indexByPath = new ConcurrentDictionary<string, List<IndexEntry>>(StringComparer.OrdinalIgnoreCase);
				_indexByChildren = new ConcurrentDictionary<Guid, List<IndexEntry>>();

				for (var i = 0; i < entries.Count; i++)
				{
					AddEntryToIndices(entries[i]);
				}

				_isInitialized = true;
			}
		}

		public virtual IndexEntry GetById(Guid itemId)
		{
			if (!_isInitialized) throw new InvalidOperationException("Index has not been initialized. Call Initialize() first.");

			IndexEntry result;
			if (_indexById.TryGetValue(itemId, out result)) return result;

			return null;
		}

		public virtual IReadOnlyCollection<IndexEntry> GetByPath(string path)
		{
			if (!_isInitialized) throw new InvalidOperationException("Index has not been initialized. Call Initialize() first.");

			List<IndexEntry> result;
			if (_indexByPath.TryGetValue(path, out result)) return result.AsReadOnly();

			return new List<IndexEntry>().AsReadOnly();
		}

		public virtual IReadOnlyCollection<IndexEntry> GetByTemplate(Guid templateId)
		{
			if (!_isInitialized) throw new InvalidOperationException("Index has not been initialized. Call Initialize() first.");

			List<IndexEntry> result;
			if (_indexByTemplate.TryGetValue(templateId, out result)) return result.AsReadOnly();

			return new List<IndexEntry>().AsReadOnly();
		}

		public virtual IReadOnlyCollection<IndexEntry> GetChildren(Guid parentId)
		{
			if (!_isInitialized) throw new InvalidOperationException("Index has not been initialized. Call Initialize() first.");

			List<IndexEntry> result;
			if (_indexByChildren.TryGetValue(parentId, out result)) return result.AsReadOnly();

			return new List<IndexEntry>().AsReadOnly();
		}

		public virtual IEnumerable<IndexEntry> GetDescendants(Guid parentId)
		{
			if (!_isInitialized) throw new InvalidOperationException("Index has not been initialized. Call Initialize() first.");

			var queue = new Queue<IndexEntry>(GetChildren(parentId));

			while (queue.Count > 0)
			{
				var current = queue.Dequeue();

				yield return current;

				var children = GetChildren(current.Id);

				foreach (var child in children)
				{
					queue.Enqueue(child);
				}
			}
		}

		public virtual IReadOnlyCollection<IndexEntry> GetAll()
		{
			if (!_isInitialized) throw new InvalidOperationException("Index has not been initialized. Call Initialize() first.");

			return _indexById.Values.ToList().AsReadOnly();
		}

		public virtual void Update(IndexEntry entry)
		{
			if (!_isInitialized) throw new InvalidOperationException("Index has not been initialized. Call Initialize() first.");

			lock (SyncRoot)
			{
				bool dirty;
				var existing = GetById(entry.Id);

				// brand new entry
				if (existing == null)
				{
					AddEntryToIndices(entry);
					dirty = true;
				}
				else if (existing.Path.Equals(entry.Path))
				{
					// no path changed means just a data update to an existing entry
					// if no index data changes, we don't mark it as dirty (saving us a write to disk later)
					dirty = MergeEntry(entry, existing);
				}
				else
				{
					// rename or move - path changed. We need to merge and rewrite descendant paths.
					Assert.AreEqual(existing.ParentPath, entry.ParentPath, "The items' parent IDs matched, but their parent paths did not. This is corrupt data.");

					MoveDescendants(entry, existing);
					MergeEntry(entry, existing);

					dirty = true;
				}

				// note that dirty is only marked if the index has actually changed some values
				if (dirty)
					IsDirty = true;
			}
		}

		public virtual bool Remove(Guid itemId)
		{
			var item = GetById(itemId);

			if (item == null) return false;

			var itemsToDelete = GetDescendants(itemId);

			var removalQueue = new Queue<IndexEntry>(itemsToDelete);
			removalQueue.Enqueue(GetById(itemId));

			while (removalQueue.Count > 0)
			{
				IndexEntry itemToRemove = removalQueue.Dequeue();

				// remove from item ID index
				IndexEntry value;
				_indexById.TryRemove(itemToRemove.Id, out value);

				// remove from template ID index
				List<IndexEntry> templateEntries;
				if (_indexByTemplate.TryGetValue(itemToRemove.TemplateId, out templateEntries))
				{
					templateEntries.RemoveAll(x => x.Id == itemToRemove.Id);
				}

				// remove from path index
				List<IndexEntry> pathEntries;
				if (_indexByPath.TryGetValue(itemToRemove.Path, out pathEntries))
				{
					pathEntries.RemoveAll(x => x.Id == itemToRemove.Id);
				}

				// remove from children index
				List<IndexEntry> childValue;
				_indexByChildren.TryRemove(itemToRemove.Id, out childValue);
			}

			return true;
		}

		/// <summary>
		/// True if the index has been changed since loading.
		/// You may set this to false manually, e.g. after flushing the index to storage successfully.
		/// </summary>
		public virtual bool IsDirty { get; set; }

		protected virtual void MoveDescendants(IndexEntry updatedEntry, IndexEntry existingEntry)
		{
			if (updatedEntry.Id != existingEntry.Id) throw new ArgumentException("Item IDs did not match on old and new. Go away.");

			var descendants = GetDescendants(existingEntry.Id);

			foreach (var descendant in descendants)
			{
				var relativePath = descendant.Path.Substring(existingEntry.Path.Length);

				descendant.Path = string.Concat(updatedEntry.Path, "/", relativePath);
			}
		}

		protected bool MergeEntry(IndexEntry newEntry, IndexEntry entryToMergeTo)
		{
			if (newEntry.Id != entryToMergeTo.Id) throw new ArgumentException("Item IDs to merge did not match. Go away.");

			bool changed = false;

			if (entryToMergeTo.ParentId != newEntry.ParentId)
			{
				entryToMergeTo.ParentId = newEntry.ParentId;
				changed = true;
			}

			if (!entryToMergeTo.Path.Equals(newEntry.Path))
			{
				entryToMergeTo.Path = newEntry.Path;
				changed = true;
			}

			if (entryToMergeTo.TemplateId != newEntry.TemplateId)
			{
				entryToMergeTo.TemplateId = newEntry.TemplateId;
				changed = true;
			}

			return changed;
		}

		protected void AddEntryToIndices(IndexEntry entry)
		{
			// this is always invoked from within a critical section (as are other modifications to the indexes)
			// and thus TryAdd() should ALWAYS be an add as the previous check on existence will have failed
			bool add = _indexById.TryAdd(entry.Id, entry);
			if (!add) throw new InvalidOperationException("Key was already in the dictionary. This should never occur.");

			var template = EnsureCollectionKey(_indexByTemplate, entry.TemplateId);
			template.Add(entry);

			var path = EnsureCollectionKey(_indexByPath, entry.Path);
			path.Add(entry);

			var children = EnsureCollectionKey(_indexByChildren, entry.ParentId);
			children.Add(entry);
		}

		protected List<IndexEntry> EnsureCollectionKey<T>(ConcurrentDictionary<T, List<IndexEntry>> dictionary, T key)
		{
			List<IndexEntry> indexEntry;
			if (dictionary.TryGetValue(key, out indexEntry)) return indexEntry;

			indexEntry = new List<IndexEntry>();
			bool add = dictionary.TryAdd(key, indexEntry);
			if (!add) throw new InvalidOperationException("Key was already in the dictionary. This should never occur.");

			return indexEntry;
		}
	}
}
