using JsonApiSerializer.ContractResolvers;
using JsonApiSerializer.ContractResolvers.Contracts;
using JsonApiSerializer.Exceptions;
using JsonApiSerializer.JsonApi;
using JsonApiSerializer.JsonApi.WellKnown;
using JsonApiSerializer.JsonConverters;
using JsonApiSerializer.SerializationState;
using JsonApiSerializer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace JsonApiSerializer.JsonConverters
{
    /// <summary>
    /// Provides functionality to convert a JsonApi resource object into a .NET object
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public class ResourceObjectConverter : JsonConverter
    {
        private static readonly Regex DataReadPathRegex = new Regex($@"^$|{PropertyNames.Included}(\[\d+\])?$|{"data"}(\[\d+\])?$");

        private static readonly List<Type> AllowedIdTypes = new List<Type> {
            typeof(string),
            typeof(int),
            typeof(long),
            typeof(Guid),
            typeof(int?),
            typeof(long?),
            typeof(Guid?),
            typeof(uint),
            typeof(ulong),
            typeof(uint?),
            typeof(ulong?),
        };

        public override bool CanConvert(Type objectType)
        {
            return TypeInfoShim.GetPropertyFromInheritanceChain(objectType.GetTypeInfo(), "Id") != null;
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            //we may be starting the deserialization here, if thats the case we need to resolve this object as the root
            var serializationData = SerializationData.GetSerializationData(reader);
            if (!serializationData.HasProcessedDocumentRoot)
                return DocumentRootConverter.ResolveAsRootData(reader, objectType, serializer);

            if (ReaderUtil.TryUseCustomConvertor(reader, objectType, existingValue, serializer, this, out object customConvertedValue))
                return customConvertedValue;

            //if the value has been explicitly set to null then the value of the element is simply null
            if (reader.TokenType == JsonToken.Null)
                return null;

            serializationData.ConverterStack.Push(this);

            var forkableReader = reader as ForkableJsonReader ?? new ForkableJsonReader(reader);
            reader = forkableReader;
            var contractResolver = (JsonApiContractResolver)serializer.ContractResolver;

            var reference = ReaderUtil.ReadAheadToIdentifyObject(forkableReader);

            //if we dont have this object already we will create it
            existingValue = existingValue ?? CreateObject(objectType, reference.Type, serializer);


            //mark this object as a possible include. We need to do this before deserialiazing
            //the relationship; It could have relationships that reference back to this object
            serializationData.Included[reference] = existingValue;

            JsonContract rawContract = contractResolver.ResolveContract(existingValue.GetType());
            if (!(rawContract is JsonObjectContract contract))
                throw new JsonApiFormatException(
                   forkableReader.FullPath,
                   $"Expected created object to be a resource object, but found '{existingValue}'",
                   "Resource indentifier objects MUST contain 'id' members");



            foreach (var propName in ReaderUtil.IterateProperties(reader))
            {
                var successfullyPopulateProperty = ReaderUtil.TryPopulateProperty(
                    serializer,
                    existingValue,
                    contract.Properties.GetClosestMatchProperty(propName),
                    reader);

                //flatten out attributes onto the object
                if (!successfullyPopulateProperty && propName == PropertyNames.Attributes)
                {
                    foreach (var innerPropName in ReaderUtil.IterateProperties(reader))
                    {
                        ReaderUtil.TryPopulateProperty(
                           serializer,
                           existingValue,
                           contract.Properties.GetClosestMatchProperty(innerPropName),
                           reader);
                    }
                }

                //flatten out relationships onto the object
                if (!successfullyPopulateProperty && propName == PropertyNames.Relationships)
                {
                    foreach (var innerPropName in ReaderUtil.IterateProperties(reader))
                    {
                        var prop = contract.Properties.GetClosestMatchProperty(innerPropName);
                        if (prop == null)
                            continue;

                        // This is a massive hack. MemberConverters used to work and allowed
                        // modifying a members create object. Unfortunatly Resource Identifiers
                        // are no longer created by this object converter. We are passing the
                        // convertor via the serialization data so ResourceIdentifierConverter
                        // can access it down the line.
                        // next breaking change remove support for ResourceObjectConverter 
                        // member converters
                        if (prop.MemberConverter != null)
                            serializationData.ConverterStack.Push(prop.MemberConverter);

                        ReaderUtil.TryPopulateProperty(
                          serializer,
                          existingValue,
                          contract.Properties.GetClosestMatchProperty(innerPropName),
                          reader,
                          overrideConverter: contractResolver.ResourceRelationshipConverter);

                        if (prop.MemberConverter != null)
                            serializationData.ConverterStack.Pop();
                    }
                }
            }

            //we have read the object so we will tag it as 'rendered'
            serializationData.RenderedIncluded.Add(reference);

            serializationData.ConverterStack.Pop();
            return existingValue;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var serializationData = SerializationData.GetSerializationData(writer);
            if (!serializationData.HasProcessedDocumentRoot)
            {
                //treat this value as a document root
                DocumentRootConverter.ResolveAsRootData(writer, value, serializer);
                return;
            }

            // if they have custom convertors registered, we will respect them
            if (WriterUtil.TryUseCustomConvertor(writer, value, serializer, excludeConverter: this))
                return;

            var jsonApiContractResolver = (JsonApiContractResolver)serializer.ContractResolver;
            var valueType = value.GetType();

            if (!(serializer.ContractResolver.ResolveContract(valueType) is ResourceObjectContract metadata))
                throw new JsonApiFormatException(
                      writer.Path,
                      $"Expected to find to find resource object, but found '{value}'",
                      "Resource indentifier objects MUST contain 'id' members");

            serializationData.ConverterStack.Push(this);

            writer.WriteStartObject();

            //serialize id
            if (WriterUtil.ShouldWriteStringProperty(writer, value, metadata.IdProperty, serializer, out string id))
            {
                writer.WritePropertyName(PropertyNames.Id);
                writer.WriteValue(id);
            }

            //serialize type. Will always out put a type
            WriterUtil.ShouldWriteStringProperty(writer, value, metadata.TypeProperty, serializer, out string type);
            type = type ?? WriterUtil.CalculateDefaultJsonApiType(value, serializationData, serializer);
            writer.WritePropertyName(PropertyNames.Type);
            writer.WriteValue(type);

            //serialize links
            if (WriterUtil.ShouldWriteProperty(value, metadata.LinksProperty, serializer, out object links))
            {
                writer.WritePropertyName(PropertyNames.Links);
                serializer.Serialize(writer, links);
            }

            //serialize meta
            if (WriterUtil.ShouldWriteProperty(value, metadata.MetaProperty, serializer, out object meta))
            {
                writer.WritePropertyName(PropertyNames.Meta);
                serializer.Serialize(writer, meta);
            }

            // store all the relationships, that appear to be attributes from the 
            // property declared type, types but the runtime type shows they are
            // actaully relationships
            List<KeyValuePair<JsonProperty, object>> undeclaredRelationships = null;

            //serialize attributes
            var startedAttributeSection = false;
            for (var i = 0; i < metadata.Attributes.Length; i++)
            {
                var attributeProperty = metadata.Attributes[i];
                if (WriterUtil.ShouldWriteProperty(value, attributeProperty, serializer, out object attributeValue))
                {
                    // some relationships are not decalred as such. They exist in properties
                    // with declared types of `object` but the runtime object within is a
                    // relationship. We will check here if this attribute property is really
                    // a relationship, and if it is store it to process later

                    // NOTE: this behviour it leads to nulls being inconsistantly attribute/relationship.
                    // leaving in for backward compatability but remove on next breaking change
                    var attributeValueType = attributeValue?.GetType();
                    if (attributeValueType != null
                        && attributeProperty.PropertyType != attributeValueType
                        && jsonApiContractResolver.ResourceRelationshipConverter.CanConvert(attributeValueType))
                    {
                        undeclaredRelationships = undeclaredRelationships ?? new List<KeyValuePair<JsonProperty, object>>();
                        undeclaredRelationships.Add(new KeyValuePair<JsonProperty, object>(attributeProperty, attributeValue));
                        continue;
                    }

                    //serialize out the attribute
                    if (!startedAttributeSection)
                    {
                        startedAttributeSection = true;
                        writer.WritePropertyName(PropertyNames.Attributes);
                        writer.WriteStartObject();
                    }
                    writer.WritePropertyName(attributeProperty.PropertyName);
                    if (attributeProperty.MemberConverter?.CanWrite == true)
                    {
                        attributeProperty.MemberConverter.WriteJson(writer, attributeValue, serializer);
                    }
                    else if (attributeValue is string attributeString)
                    {
                        writer.WriteValue(attributeString);
                    }
                    else if (attributeValue is bool attributeBool)
                    {
                        writer.WriteValue(attributeBool);
                    }
                    else if (attributeValue is int attributeInt)
                    {
                        writer.WriteValue(attributeValue);
                    }
                    else
                    {
                        serializer.Serialize(writer, attributeValue);
                    }
                }
            }
            if (startedAttributeSection)
                writer.WriteEndObject();

            //serialize relationships
            var startedRelationshipSection = false;

            //first go through our relationships that were originally declared as attributes
            for (var i = 0; undeclaredRelationships != null && i < undeclaredRelationships.Count; i++)
            {
                var relationshipProperty = undeclaredRelationships[i].Key;
                var relationshipValue = undeclaredRelationships[i].Value;
                if (!startedRelationshipSection)
                {
                    startedRelationshipSection = true;
                    writer.WritePropertyName(PropertyNames.Relationships);
                    writer.WriteStartObject();
                }

                if (relationshipProperty.MemberConverter != null)
                    serializationData.ConverterStack.Push(relationshipProperty.MemberConverter);

                writer.WritePropertyName(relationshipProperty.PropertyName);
                jsonApiContractResolver.ResourceRelationshipConverter.WriteNullableJson(
                    writer,
                    relationshipProperty.PropertyType,
                    relationshipValue,
                    serializer);

                if (relationshipProperty.MemberConverter != null)
                    serializationData.ConverterStack.Pop();

            }

            //then go through the ones we know to be relationships
            for (var i = 0; i < metadata.Relationships.Length; i++)
            {
                var relationshipProperty = metadata.Relationships[i];
                if (WriterUtil.ShouldWriteProperty(value, relationshipProperty, serializer, out object relationshipValue))
                {
                    if (!startedRelationshipSection)
                    {
                        startedRelationshipSection = true;
                        writer.WritePropertyName(PropertyNames.Relationships);
                        writer.WriteStartObject();
                    }

                    if (relationshipProperty.MemberConverter != null)
                        serializationData.ConverterStack.Push(relationshipProperty.MemberConverter);

                    writer.WritePropertyName(relationshipProperty.PropertyName);
                    jsonApiContractResolver.ResourceRelationshipConverter.WriteNullableJson(
                        writer,
                        relationshipProperty.PropertyType,
                        relationshipValue,
                        serializer);

                    if (relationshipProperty.MemberConverter != null)
                        serializationData.ConverterStack.Pop();
                }
            }
            if (startedRelationshipSection)
                writer.WriteEndObject();

            writer.WriteEndObject();

            //add reference to this type, so others can reference it
            if (id != null)
            {
                var reference = new ResourceObjectReference(id, type);
                serializationData.RenderedIncluded.Add(reference);
            }

            serializationData.ConverterStack.Pop();
        }


        /// <summary>
        /// If there is no Type property on the item then this is called to generate a default Type name
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        protected virtual string GenerateDefaultTypeName(Type type)
        {
            return type.Name.ToLowerInvariant();
        }

        internal string GenerateDefaultTypeNameInternal(Type type)
        {
            return GenerateDefaultTypeName(type);
        }

        /// <summary>
        /// Exposes contract to allow overriding object initialisation based on resource type during deserialization.
        /// </summary>
        /// <param name="objectType">Type of the property that the created object will be assigned to</param>
        /// <param name="jsonapiType">Type field specified on on the json api document</param>
        /// <param name="serializer">The serializer.</param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        protected virtual object CreateObject(Type objectType, string jsonapiType, JsonSerializer serializer)
        {
            var contract = serializer.ContractResolver.ResolveContract(objectType);
            if (contract.DefaultCreator == null)
            {
                var typeInfo = objectType.GetTypeInfo();
                if (typeInfo.IsInterface)
                    throw new JsonSerializationException($"Could not create an instance of type {objectType}. Type is an interface and cannot be instantiated.");
                else if (typeInfo.IsAbstract)
                    throw new JsonSerializationException($"Could not create an instance of type {objectType}. Type is an abstract class and cannot be instantiated.");
                else
                    throw new JsonSerializationException($"Could not create an instance of type {objectType}.");
            }

            return contract.DefaultCreator();
        }

        internal object CreateObjectInternal(Type objectType, string jsonapiType, JsonSerializer serializer)
        {
            return this.CreateObject(objectType, jsonapiType, serializer);
        }
    }
}
