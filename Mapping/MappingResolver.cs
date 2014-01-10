﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Metadata.Edm;
using System.Data.Objects;
using System.Linq;
using System.Web;

namespace EntityFramework.Mapping
{
	/// <summary>
	/// Static class to resolve Entity Framework mapping.
	/// </summary>
	public static class MappingResolver
	{
		private static readonly ConcurrentDictionary<string, Dictionary<Type, EntitySet>> _entitySetMappings;
		private static readonly ConcurrentDictionary<Type, EntityMap> _entityMapping;

		static MappingResolver()
		{
			_entitySetMappings = new ConcurrentDictionary<string, Dictionary<Type, EntitySet>>();
			_entityMapping = new ConcurrentDictionary<Type, EntityMap>();
		}

		public static readonly object _synGetEntityMapObj = new object();
		/// <summary>
		/// Gets an <see cref="EntityMap"/> for the entity type used in the specified <paramref name="query"/>.
		/// </summary>
		/// <typeparam name="TEntity">The type of the entity.</typeparam>
		/// <param name="query">The query used to create the <see cref="EntityMap"/> from.</param>
		/// <returns>An <see cref="EntityMap"/> for the specified <paramref name="query"/>.</returns>
		public static EntityMap GetEntityMap<TEntity>(this ObjectQuery query)
		{
			Type type = typeof(TEntity);
			EntityMap entityMap = _entityMapping.GetOrAdd(
				type,
				k =>
				{
					var provider = Locator.Current.Resolve<IMappingProvider>();
					return provider.GetEntityMap<TEntity>(query);
				});

			if (entityMap == null)
			{
				lock (_synGetEntityMapObj)
				{
					if (entityMap == null)
					{
						var provider = Locator.Current.Resolve<IMappingProvider>();
						entityMap = provider.GetEntityMap<TEntity>(query);
						if (entityMap == null)
						{
							HttpRuntime.UnloadAppDomain();
							throw new ArgumentNullException("依然还是不能读到映射信息，还需要研究", "EntityFramework.Extended entityMap");
							
						}
						if (_entityMapping.ContainsKey(type))
						{
							_entityMapping[type] = entityMap;
						}
						else
						{
							_entityMapping.TryAdd(type, entityMap);
						}
					}
				}
			}
			return entityMap;

		}
		
		/// <summary>
		/// Gets the underling <see cref="EntitySet"/> for the type of entity.
		/// </summary>
		/// <typeparam name="TEntity">The type of the entity.</typeparam>
		/// <param name="objectContext">The <see cref="ObjectContext"/> context to get the <see cref="EntitySet"/> from.</param>
		/// <returns>The <see cref="EntitySet"/> for the type of entity specified.</returns>
		public static EntitySet GetEntitySet<TEntity>(this ObjectContext objectContext)
		{
			var entityType = typeof(TEntity);
			var mapping = _entitySetMappings.GetOrAdd(objectContext.DefaultContainerName, k =>
			{
				var metadataWorkspace = objectContext.MetadataWorkspace;
				// make sure types are loaded
				Type baseType = entityType;
				do
				{
					metadataWorkspace.LoadFromAssembly(baseType.Assembly);
					baseType = baseType.BaseType;
				} while ((baseType != null) && (baseType != typeof(object)));

				return CreateEntitySetMappings(metadataWorkspace);
			});

			EntitySet entitySet;
			mapping.TryGetValue(entityType, out entitySet);
			return entitySet;
		}

		private static Dictionary<Type, EntitySet> CreateEntitySetMappings(MetadataWorkspace metadataWorkspace)
		{
			var entitySetMappings = new Dictionary<Type, EntitySet>();
			var itemCollection = (ObjectItemCollection)metadataWorkspace.GetItemCollection(DataSpace.OSpace);
			var entityTypes = metadataWorkspace.GetItems<EntityType>(DataSpace.OSpace);
			var entityContainers = metadataWorkspace.GetItems<EntityContainer>(DataSpace.CSpace);
			var stack = new Stack<EntityType>();

			if (entityTypes == null || entityContainers == null)
				return entitySetMappings;

			foreach (EntityType type in entityTypes)
			{
				Func<EntitySetBase, bool> predicate = null;
				stack.Clear();
				var cspaceType = (EntityType)metadataWorkspace.GetEdmSpaceType(type);
				do
				{
					stack.Push(cspaceType);
					cspaceType = (EntityType)cspaceType.BaseType;
				} while (cspaceType != null);

				EntitySet entitySet = null;
				while ((entitySet == null) && (stack.Count > 0))
				{
					cspaceType = stack.Pop();
					foreach (EntityContainer container in entityContainers)
					{
						if (predicate == null)
							predicate = s => s.ElementType == cspaceType;

						var source = container.BaseEntitySets
							.Where(predicate)
							.ToList();

						int count = source.Count();
						if ((count > 1) || ((count == 1) && (entitySet != null)))
							throw new InvalidOperationException("Multiple entity sets per type is not supported.");
						if (count == 1)
							entitySet = (EntitySet)source.First();
					}
				}

				if (entitySet == null)
					continue;

				Type clrType = itemCollection.GetClrType(type);
				entitySetMappings[clrType] = entitySet;
			}

			return entitySetMappings;
		}
	}
}
