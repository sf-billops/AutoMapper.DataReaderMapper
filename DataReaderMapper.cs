﻿namespace AutoMapper.DataReaderMapper
{
   using System;
   using System.Collections;
   using System.Collections.Concurrent;
   using System.Collections.Generic;
   using System.Data;
   using System.Linq;
   using System.Linq.Expressions;
   using System.Reflection;
   using System.Reflection.Emit;
   using AutoMapper;
   using AutoMapper.Internal;
   using AutoMapper.Mappers;
#if DOTNET
    using IDataRecord = System.Data.Common.DbDataReader;
    using IDataReader = System.Data.Common.DbDataReader;
#endif

   public class DataReaderMapper : IObjectMapper
   {
      private static ConcurrentDictionary<BuilderKey, Build> _builderCache = new ConcurrentDictionary<BuilderKey, Build>();
      private static ConcurrentDictionary<Type, CreateEnumerableAdapter> _enumerableAdapterCache = new ConcurrentDictionary<Type, CreateEnumerableAdapter>();

      public bool YieldReturnEnabled { get; set; }
      private static IMappingEngine Mapper { get; set; }

      public DataReaderMapper(IMappingEngine mapper)
      {
         Mapper = mapper;
      }

      public object Map(ResolutionContext context)
      {

         if (IsDataReader(context))
         {
            var destinationElementType = TypeHelper.GetElementType(context.DestinationType);
            var results = MapDataReaderToEnumerable(context, Mapper, destinationElementType, YieldReturnEnabled);

            if (YieldReturnEnabled)
            {
               var adapterBuilder = GetDelegateToCreateEnumerableAdapter(destinationElementType);
               return adapterBuilder(results);
            }

            return results;
         }

         if (IsDataRecord(context))
         {
            var dataRecord = context.SourceValue as IDataRecord;
            var buildFrom = CreateBuilder(context.DestinationType, dataRecord);
            var result = buildFrom(dataRecord);
            MapPropertyValues(context, Mapper, result);
            return result;
         }

         return null;
      }

      static IEnumerable MapDataReaderToEnumerable(ResolutionContext context, IMappingEngine mapper, Type destinationElementType, bool useYieldReturn)
      {
         var dataReader = (IDataReader)context.SourceValue;
         var resolveUsingContext = context;

         if (context.TypeMap == null)
         {
            var configurationProvider = mapper.ConfigurationProvider;
            TypeMap typeMap = configurationProvider.FindTypeMapFor(context.SourceType, destinationElementType);
            resolveUsingContext = new ResolutionContext(typeMap, context.SourceValue, context.SourceType, destinationElementType, context.Options, (IMappingEngine)mapper);
         }

         var buildFrom = CreateBuilder(destinationElementType, dataReader);

         if (useYieldReturn)
            return LoadDataReaderViaYieldReturn(dataReader, mapper, buildFrom, resolveUsingContext);

         return LoadDataReaderViaList(dataReader, mapper, buildFrom, resolveUsingContext, destinationElementType);
      }

      static IEnumerable LoadDataReaderViaList(IDataReader dataReader, IMappingEngine mapper, Build buildFrom, ResolutionContext resolveUsingContext, Type elementType)
      {
         var list = ObjectCreator.CreateList(elementType);

         while (dataReader.Read())
         {
            var result = buildFrom(dataReader);
            MapPropertyValues(resolveUsingContext, mapper, result);
            list.Add(result);
         }

         return list;
      }

      static IEnumerable LoadDataReaderViaYieldReturn(IDataReader dataReader, IMappingEngine mapper, Build buildFrom, ResolutionContext resolveUsingContext)
      {
         while (dataReader.Read())
         {
            var result = buildFrom(dataReader);
            MapPropertyValues(resolveUsingContext, mapper, result);
            yield return result;
         }
      }


      public bool IsMatch(TypePair context)
      {
         return IsDataReader(context) || IsDataRecord(context);
      }

      private static bool IsDataReader(TypePair typePair)
      {
         return typeof(IDataReader).IsAssignableFrom(typePair.SourceType) &&
                typePair.DestinationType.IsEnumerableType();
      }

      private static bool IsDataReader(ResolutionContext context)
      {
         return typeof(IDataReader).IsAssignableFrom(context.SourceType) &&
                context.DestinationType.IsEnumerableType();
      }

      private static bool IsDataRecord(TypePair typePair)
      {
         return typeof(IDataRecord).IsAssignableFrom(typePair.SourceType);
      }

      private static bool IsDataRecord(ResolutionContext context)
      {
         return typeof(IDataRecord).IsAssignableFrom(context.SourceType);
      }

      private static Build CreateBuilder(Type destinationType, IDataRecord dataRecord)
      {
         Build builder;
         BuilderKey builderKey = new BuilderKey(destinationType, dataRecord);
         if (_builderCache.TryGetValue(builderKey, out builder))
         {
            return builder;
         }

         var drFieldNames = new List<string>(dataRecord.FieldCount);
         var bindingFlags = BindingFlags.Public | BindingFlags.IgnoreCase | BindingFlags.Instance;
         var method = new DynamicMethod("DynamicCreate", destinationType, new[] { typeof(IDataRecord) }, destinationType, true);
         var generator = method.GetILGenerator();

         var result = generator.DeclareLocal(destinationType);
         generator.Emit(OpCodes.Newobj, destinationType.GetConstructor(Type.EmptyTypes));
         generator.Emit(OpCodes.Stloc, result);

         for (var i = 0; i < dataRecord.FieldCount; i++)
         {
            drFieldNames.Add(dataRecord.GetName(i));
            var propertyInfo = destinationType.GetProperty(dataRecord.GetName(i), bindingFlags);
            GetSetProperty(dataRecord, generator, result, i, propertyInfo, OpCodes.Ldarg_0, null);
         }

         var nestedProperties = drFieldNames
             .Where(name => name.Contains("."))
             .OrderBy(x => x)
             .Select(x => x.Split('.'))
             .ToLookup(x => string.Join(".", x.Take(x.Length - 1)), element => element.Last());

         foreach (var nestedProperty in nestedProperties)
         {
            var propInfo = destinationType.GetProperty(nestedProperty.Key, bindingFlags);
            if (propInfo == null || !propInfo.CanWrite || propInfo.PropertyType.IsValueType())
               continue;

            var prop = generator.DeclareLocal(propInfo.PropertyType);
            generator.Emit(OpCodes.Newobj, propInfo.PropertyType.GetConstructor(Type.EmptyTypes));
            generator.Emit(OpCodes.Stloc, prop);
            var counter = generator.DeclareLocal(typeof(int));

            foreach (var np in nestedProperty)
            {
               var drIdx = drFieldNames.IndexOf("{nestedProperty.Key}.{np}");
               if (drIdx == -1)
                  continue;

               var innerPropInfo = propInfo.PropertyType.GetProperty(np, bindingFlags);
               GetSetProperty(dataRecord, generator, prop, drIdx, innerPropInfo, OpCodes.Ldarg_1, counter);
            }

            // if counter > 0 set the property to the object
            generator.Emit(OpCodes.Ldloc, counter);
            generator.Emit(OpCodes.Ldc_I4_0);
            generator.Emit(OpCodes.Cgt); // counter > 0
            generator.Emit(OpCodes.Ldc_I4_0);
            var endIf = generator.DefineLabel();
            generator.Emit(OpCodes.Beq_S, endIf);
            // { result = prop;
            generator.Emit(OpCodes.Ldloc, result);
            generator.Emit(OpCodes.Ldloc, prop);
            generator.Emit(OpCodes.Callvirt, propInfo.GetSetMethod(true));
            // }
            generator.MarkLabel(endIf);
         }

         generator.Emit(OpCodes.Ldloc, result);
         generator.Emit(OpCodes.Ret);
         builder = (Build)method.CreateDelegate(typeof(Build));
         _builderCache[builderKey] = builder;
         return builder;
      }

      private static void GetSetProperty(IDataRecord dataRecord, ILGenerator generator, LocalBuilder result, int i, PropertyInfo propertyInfo, OpCode loadArg, LocalBuilder counter)
      {
         var endIfLabel = generator.DefineLabel();

         if (propertyInfo != null && propertyInfo.GetSetMethod(true) != null)
         {
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldc_I4, i);
            generator.Emit(OpCodes.Callvirt, isDBNullMethod);
            generator.Emit(OpCodes.Brtrue, endIfLabel);

            generator.Emit(OpCodes.Ldloc, result);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldc_I4, i);
            generator.Emit(OpCodes.Callvirt, getValueMethod);

            if (propertyInfo.PropertyType.IsGenericType()
                && propertyInfo.PropertyType.GetGenericTypeDefinition().Equals(typeof(Nullable<>))
                )
            {
               var nullableType = propertyInfo.PropertyType.GetGenericTypeDefinition().GetGenericArguments()[0];
               if (!nullableType.IsEnum())
                  generator.Emit(OpCodes.Unbox_Any, propertyInfo.PropertyType);
               else
               {
                  generator.Emit(OpCodes.Unbox_Any, nullableType);
                  generator.Emit(OpCodes.Newobj, propertyInfo.PropertyType);
               }
            }
            else
            {
               generator.Emit(OpCodes.Unbox_Any, dataRecord.GetFieldType(i));
            }
            generator.Emit(OpCodes.Callvirt, propertyInfo.GetSetMethod(true));

            if (counter != null)
            {
               generator.Emit(OpCodes.Ldloc, counter);
               generator.Emit(OpCodes.Ldc_I4_1);
               generator.Emit(OpCodes.Add);
               generator.Emit(OpCodes.Stloc, counter);
            }

            generator.MarkLabel(endIfLabel);
         }
      }

      private static void MapPropertyValues(ResolutionContext context, IMappingEngine mapper, object result)
      {
         if (context.TypeMap == null)
            throw new AutoMapperMappingException(context, "Missing type map configuration or unsupported mapping.");

         foreach (var propertyMap in context.TypeMap.GetPropertyMaps())
         {
            MapPropertyValue(context, mapper, result, propertyMap);
         }
      }

      private static void MapPropertyValue(ResolutionContext context, IMappingEngine mapper, object mappedObject, PropertyMap propertyMap)
      {
         if (!propertyMap.CanResolveValue())
            return;

         var result = propertyMap.ResolveValue(context);
         var newContext = context.CreateMemberContext(null, result.Value, null, result.Type, propertyMap);

         if (!propertyMap.ShouldAssignValue(newContext))
            return;

         try
         {
            var propertyValueToAssign = mapper.Map(newContext);

            if (propertyMap.CanBeSet)
               propertyMap.DestinationProperty.SetValue(mappedObject, propertyValueToAssign);
         }
         catch (AutoMapperMappingException)
         {
            throw;
         }
         catch (Exception ex)
         {
            throw new AutoMapperMappingException(newContext, ex);
         }
      }

      private static CreateEnumerableAdapter GetDelegateToCreateEnumerableAdapter(Type elementType)
      {
         CreateEnumerableAdapter builder;
         if (_enumerableAdapterCache.TryGetValue(elementType, out builder))
         {
            return builder;
         }

         var adapterType = typeof(EnumerableAdapter<>).MakeGenericType(elementType);
         var adapterCtor = adapterType.GetConstructor(new[] { typeof(IEnumerable) });
         var adapterCtorArg = Expression.Parameter(typeof(IEnumerable), "items");
         var adapterCtorExpression = Expression.New(adapterCtor, adapterCtorArg);
         builder = (CreateEnumerableAdapter)Expression.Lambda(typeof(CreateEnumerableAdapter), adapterCtorExpression, adapterCtorArg).Compile();

         _enumerableAdapterCache[elementType] = builder;
         return builder;
      }

      private delegate object Build(IDataRecord dataRecord);
      private delegate object CreateEnumerableAdapter(IEnumerable items);

      private static readonly MethodInfo getValueMethod = typeof(IDataRecord).GetMethod("get_Item", new[] { typeof(int) });
      private static readonly MethodInfo isDBNullMethod = typeof(IDataRecord).GetMethod("IsDBNull", new[] { typeof(int) });

      private class BuilderKey
      {
         private readonly List<string> _dataRecordNames;
         private readonly Type _destinationType;

         public BuilderKey(Type destinationType, IDataRecord record)
         {
            _destinationType = destinationType;
            _dataRecordNames = new List<string>(record.FieldCount);
            for (int i = 0; i < record.FieldCount; i++)
            {
               _dataRecordNames.Add(record.GetName(i));
            }
         }

         public override int GetHashCode()
         {
            int hash = _destinationType.GetHashCode();
            foreach (var name in _dataRecordNames)
            {
               hash = hash * 37 + name.GetHashCode();
            }
            return hash;
         }

         public override bool Equals(object obj)
         {
            var builderKey = obj as BuilderKey;
            if (builderKey == null)
               return false;

            if (_dataRecordNames.Count != builderKey._dataRecordNames.Count)
               return false;

            if (_destinationType != builderKey._destinationType)
               return false;

            for (int i = 0; i < _dataRecordNames.Count; i++)
            {
               if (this._dataRecordNames[i] != builderKey._dataRecordNames[i])
                  return false;
            }
            return true;
         }
      }

      private class EnumerableAdapter<Item> : IEnumerable<Item>
      {
         IEnumerable<Item> _items;

         public EnumerableAdapter(IEnumerable items)
         {
            _items = items.Cast<Item>();
         }

         public IEnumerator<Item> GetEnumerator()
         {
            return _items.GetEnumerator();
         }

         IEnumerator IEnumerable.GetEnumerator()
         {
            return GetEnumerator();
         }
      }

   }
}