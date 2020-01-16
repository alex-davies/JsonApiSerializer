using JsonApiSerializer.ContractResolvers;
using JsonApiSerializer.JsonApi;
using JsonApiSerializer.JsonApi.WellKnown;
using JsonApiSerializer.SerializationState;
using JsonApiSerializer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace JsonApiSerializer.JsonConverters
{
    internal class DocumentRootConverter : JsonConverter
    {
        public static bool CanConvertStatic(Type objectType)
        {
            return TypeInfoShim.GetInterfaces(objectType.GetTypeInfo())
                .Select(x => x.GetTypeInfo())
                .Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IDocumentRoot<>));
        }

        public override bool CanConvert(Type objectType)
        {
            return CanConvertStatic(objectType);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var serializationData = SerializationData.GetSerializationData(reader);

            reader = new ForkableJsonReader(reader);

            var contract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(objectType);
            var rootObject = contract.DefaultCreator();
            serializationData.HasProcessedDocumentRoot = true;

            var includedConverter = new IncludedConverter();

            foreach (var propName in ReaderUtil.IterateProperties(reader))
            {
                switch (propName)
                {
                    case PropertyNames.Data:

                        var documentRootInterfaceType = TypeInfoShim.GetInterfaces(objectType.GetTypeInfo())
                            .Select(x => x.GetTypeInfo())
                            .FirstOrDefault(x =>
                                x.IsGenericType
                                && x.GetGenericTypeDefinition() == typeof(IDocumentRoot<>));
                        var dataType = documentRootInterfaceType.GenericTypeArguments[0];

                        var dataObj = serializer.Deserialize(reader, dataType);
                        contract.Properties.GetClosestMatchProperty(PropertyNames.Data).ValueProvider.SetValue(rootObject, dataObj);
                        break;
                    case PropertyNames.Included:

                        //if our object has an included property we will do our best to populate it
                        var property = contract.Properties.GetClosestMatchProperty(propName);
                        if (ReaderUtil.CanPopulateProperty(property))
                        {
                            ReaderUtil.TryPopulateProperty(serializer, rootObject, contract.Properties.GetClosestMatchProperty(propName), ((ForkableJsonReader)reader).Fork());
                        }

                        //still need to read our values so they are updated
                        foreach (var _ in ReaderUtil.IterateList(reader))
                        {
                            includedConverter.ReadJson(reader, typeof(object), null, serializer);
                        }

                        break;
                    default:
                        ReaderUtil.TryPopulateProperty(serializer, rootObject, contract.Properties.GetClosestMatchProperty(propName), reader);
                        break;
                }
            }

            for(var i=0; i< serializationData.PostProcessingActions.Count;i++)
            {
                serializationData.PostProcessingActions[i]();
            }
            serializationData.PostProcessingActions.Clear();

            return rootObject;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var serializationData = SerializationData.GetSerializationData(writer);

            serializationData.HasProcessedDocumentRoot = true;

            var contractResolver = (JsonApiContractResolver)serializer.ContractResolver;
            var contract = (JsonObjectContract)contractResolver.ResolveContract(value.GetType());
            writer.WriteStartObject();

            var propertiesOutput = new HashSet<string>();
            foreach (var prop in contract.Properties)
            {
                //we will do includes last, so we we can ensure all the references have been added
                if (prop.PropertyName == PropertyNames.Included)
                    continue;

                //respect the serializers null handling value
                var propValue = prop.ValueProvider.GetValue(value);
                if (propValue == null && (prop.NullValueHandling ?? serializer.NullValueHandling) == NullValueHandling.Ignore)
                    continue;
                var propType = propValue?.GetType() ?? prop.PropertyType;
                switch (prop.PropertyName)
                {
                    case PropertyNames.Data when ListUtil.IsList(propType, out var elementType):
                        writer.WritePropertyName(prop.PropertyName);
                        propertiesOutput.Add(prop.PropertyName);

                        if (propValue == null)
                        {
                            //Resource linkage MUST be represented by an empty array ([]) for empty to-many relationships
                            writer.WriteStartArray();
                            writer.WriteEndArray();
                            break;
                        }
                        contractResolver.ResourceObjectListConverter.WriteJson(writer, propValue, serializer);
                        break;
                    case PropertyNames.Data:
                        writer.WritePropertyName(prop.PropertyName);
                        propertiesOutput.Add(prop.PropertyName);

                        if (propValue == null)
                        {
                            writer.WriteNull();
                            break;
                        }

                        //because we are in a relationship we want to force this list to be treated as a resource object
                        contractResolver.ResourceObjectConverter.WriteJson(writer, propValue, serializer);
                        break;
                    default:
                        //A document MAY contain any of these top-level members: jsonapi, links, included
                        //We are also allowing everything else they happen to have on the root document
                        writer.WritePropertyName(prop.PropertyName);
                        serializer.Serialize(writer, propValue);
                        propertiesOutput.Add(prop.PropertyName);
                        break;
                }
            }

            //A document MUST contain one of the following (data, errors, meta)
            //so if we do not have one of them we will output a null data
            if (!propertiesOutput.Contains(PropertyNames.Data)
                && !propertiesOutput.Contains(PropertyNames.Errors)
                && !propertiesOutput.Contains(PropertyNames.Meta))
            {
                propertiesOutput.Add(PropertyNames.Data);
                writer.WritePropertyName(PropertyNames.Data);
                writer.WriteNull();
            }

            //If a document does not contain a top-level data key, the included member MUST NOT be present
            if (propertiesOutput.Contains(PropertyNames.Data))
            {
                //output the included. If we have a specified included field we will out everything in there
                //and we will also output all the references defined in our reference resolver
                var referencesToInclude = serializationData.Included
                    .Where(x => !serializationData.RenderedIncluded.Contains(x.Key)); //dont output values we have already output

                //if any other included have been explicitly mentioned we will output them as well
                var includedProperty = contract.Properties.GetClosestMatchProperty(PropertyNames.Included);
                var includedValues = includedProperty?.ValueProvider?.GetValue(value) as IEnumerable<object> ?? Enumerable.Empty<object>();

                if (referencesToInclude.Any() || includedValues.Any())
                {
                    writer.WritePropertyName(PropertyNames.Included);
                    writer.WriteStartArray();

                    foreach (var includedValue in includedValues)
                        serializer.Serialize(writer, includedValue);

                    //I know we can alter the OrderedDictionary while enumerating it, otherwise this would error
                    foreach (var includedReference in referencesToInclude)
                        serializer.Serialize(writer, includedReference.Value);

                    writer.WriteEndArray();
                }
            }

            writer.WriteEndObject();

            for (var i = 0; i < serializationData.PostProcessingActions.Count; i++)
            {
                serializationData.PostProcessingActions[i]();
            }
            serializationData.PostProcessingActions.Clear();
        }

        internal static IEnumerable<IError> ResolveAsRootError(JsonReader reader, Type objectType, JsonSerializer serializer)
        {
            //determine the error class type. The type passed in could be an array or an object
            //so we need to determine the error type for both
            Type errorElementType;
            if (!ListUtil.IsList(objectType, out errorElementType))
                errorElementType = objectType;

            //we do not have a root object, so this is probably the entry point, so we will resolve
            //a document root and return the data object
            var documentRootType = typeof(MinimalDocumentRoot<,>).MakeGenericType(typeof(object), errorElementType);
            var objContract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(documentRootType);
            var dataProp = objContract.Properties.GetClosestMatchProperty("errors");

            var root = serializer.Deserialize(reader, documentRootType);
            return (IEnumerable<IError>)dataProp.ValueProvider.GetValue(root);
        }

        internal static void ResolveAsRootError(JsonWriter writer, object value, JsonSerializer serializer)
        {
            //coerce single element into a list
            if (value is IError)
                value = new List<IError>() { (IError)value };

            //we do not have a root object, so this is probably the entry point, so we will resolve
            //it as a document root
            var documentRootType = typeof(MinimalDocumentRoot<,>).MakeGenericType(typeof(object), typeof(IError));
            var objContract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(documentRootType);
            var rootObj = objContract.DefaultCreator();

            //set the data property to be our current object
            var dataProp = objContract.Properties.GetClosestMatchProperty("errors");
            dataProp.ValueProvider.SetValue(rootObj, value);

            serializer.Serialize(writer, rootObj);
        }

        internal static object ResolveAsRootData(JsonReader reader, Type objectType, JsonSerializer serializer)
        {
            var documentRootType = typeof(MinimalDocumentRoot<,>).MakeGenericType(objectType, typeof(Error));
            var objContract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(documentRootType);
            var dataProp = objContract.Properties.GetClosestMatchProperty("data");

            var root = serializer.Deserialize(reader, documentRootType);
            return root != null ? dataProp.ValueProvider.GetValue(root) : null;
        }

        internal static void ResolveAsRootData(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var documentRootType = typeof(MinimalDocumentRoot<,>).MakeGenericType(value.GetType(), typeof(Error));
            var objContract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(documentRootType);
            var rootObj = objContract.DefaultCreator();

            //set the data property to be our current object
            var dataProp = objContract.Properties.GetClosestMatchProperty("data");
            dataProp.ValueProvider.SetValue(rootObj, value);

            serializer.Serialize(writer, rootObj);
        }

        private class MinimalDocumentRoot<TData, TError> : IDocumentRoot<TData> where TError : IError
        {
            public TData Data { get; set; }

            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public IEnumerable<TError> Errors { get; set; }
        }
    }
}
