using JsonApiSerializer.ContractResolvers;
using JsonApiSerializer.ContractResolvers.Contracts;
using JsonApiSerializer.Exceptions;
using JsonApiSerializer.JsonApi;
using JsonApiSerializer.JsonApi.WellKnown;
using JsonApiSerializer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace JsonApiSerializer.JsonConverters
{
    internal class ResourceRelationshipConverter : JsonConverter
    {
        private readonly Func<Type, bool> isResourceIdentifier;

        public ResourceRelationshipConverter(Func<Type, bool> isResourceIdentifier)
        {
            this.isResourceIdentifier = isResourceIdentifier;
        }

        public override bool CanConvert(Type objectType)
        {
            return IsExplicitRelationship(objectType)
                || isResourceIdentifier(objectType);
        }

        internal bool IsExplicitRelationship(Type objectType)
        {
            var typeInfo = objectType.GetTypeInfo();
            return TypeInfoShim.GetPropertyFromInheritanceChain(typeInfo, PropertyNames.Data) != null
                || TypeInfoShim.GetPropertyFromInheritanceChain(typeInfo, PropertyNames.Links) != null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var jsonApiContractResolver = (JsonApiContractResolver)serializer.ContractResolver;

            var type = value.GetType();
            var contract = jsonApiContractResolver.ResolveContract(type);

            //if we are not an explicit relationship then just write
            //the value out in a data field
            if(!(contract is ResourceRelationshipContract rrc))
            {
                //assume the relationship object is implied so will just write a data field
                writer.WriteStartObject();

                writer.WritePropertyName(PropertyNames.Data);
                jsonApiContractResolver.ResourceIdentifierConverter.WriteJson(writer, value, serializer);

                writer.WriteEndObject();
                return;
            }

           
            writer.WriteStartObject();
            var hasMandatoryField = false;
            for (var i = 0; i < rrc.Properties.Count; i++)
            {
                var relationshipProp = rrc.Properties[i];
                if (WriterUtil.ShouldWriteProperty(value, relationshipProp, serializer, out object propValue))
                {
                    writer.WritePropertyName(relationshipProp.PropertyName);
                    switch (relationshipProp.PropertyName)
                    {
                        case PropertyNames.Data:
                            hasMandatoryField = true;
                            if (propValue == null)
                                WriteNullOrEmpty(writer, relationshipProp.PropertyType, serializer);
                            else
                                jsonApiContractResolver.ResourceIdentifierConverter.WriteJson(writer, propValue, serializer);
                            break;
                        case PropertyNames.Links:
                        case PropertyNames.Meta:
                            hasMandatoryField = true;
                            serializer.Serialize(writer, propValue);
                            break;
                        default:
                            serializer.Serialize(writer, propValue);
                            break;
                    }
                }
            }

            //A "relationship object" MUST contain at least one of the following links, data, meta
            if (!hasMandatoryField)
            {
                writer.WritePropertyName(PropertyNames.Data);
                WriteNullOrEmpty(writer, rrc.DataProperty.PropertyType, serializer);
            }

            
            writer.WriteEndObject();

        }

        public void WriteNullableJson(JsonWriter writer, Type declaredType, object value, JsonSerializer serializer)
        {
            // WriteJson should NEVER be passed a null a value, 
            // so we will handle nulls seperately here
            if(value == null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName(PropertyNames.Data);
                WriteNullOrEmpty(writer, declaredType, serializer);
                writer.WriteEndObject();
            }
            else
            {
                WriteJson(writer, value, serializer);
            }
                
        }

        private void WriteNullOrEmpty(JsonWriter writer, Type declaredType, JsonSerializer serializer)
        {
            var contract = serializer.ContractResolver.ResolveContract(declaredType);

            //if its a null relationship, we want to know what hte data field was
            if (contract is ResourceRelationshipContract rrc)
                contract = serializer.ContractResolver.ResolveContract(rrc.DataProperty.PropertyType);
            
            if (contract is JsonArrayContract)
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNull();
            }
            
        }

        public override bool CanRead => true;

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var jsonApiContractResolver = (JsonApiContractResolver)serializer.ContractResolver;
            var contract = jsonApiContractResolver.ResolveContract(objectType);

            // we be a ResourceObject rather than a RelationshpObject
            // if so we will just read the data property of the resource object
            if (!(contract is ResourceRelationshipContract rrc))
            {
                return ReadJsonDataPropertyAsResourceObject(reader, objectType, existingValue, serializer, jsonApiContractResolver);
            }


            if (ReaderUtil.TryUseCustomConvertor(reader, objectType, existingValue, serializer, this, out object customConvertedValue))
                return customConvertedValue;

            // if the value has been explicitly set to null then the value of the element is simply null
            if (reader.TokenType == JsonToken.Null)
                return null;

            //create a new relationship object and start populating the properties
            var obj = rrc.DefaultCreator();
            
            foreach (var propName in ReaderUtil.IterateProperties(reader))
            {
                switch (propName)
                {
                    case PropertyNames.Data:
                        ReaderUtil.TryPopulateProperty(
                           serializer,
                           obj,
                           rrc.Properties.GetClosestMatchProperty(propName),
                           reader,
                           overrideConverter: jsonApiContractResolver.ResourceIdentifierConverter);
                        break;
                    default:
                        ReaderUtil.TryPopulateProperty(
                           serializer,
                           obj,
                           rrc.Properties.GetClosestMatchProperty(propName),
                           reader);
                        break;
                }
            }
            return obj;
        }

        private static object ReadJsonDataPropertyAsResourceObject(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer, JsonApiContractResolver jsonApiContractResolver)
        {
            object resourceObject = null;
            var isValid = false;
            foreach (var propName in ReaderUtil.IterateProperties(reader))
            {
                switch (propName)
                {
                    case PropertyNames.Data:
                        isValid = true;
                        // let the resource identifier deal with the rest
                        resourceObject = jsonApiContractResolver.ResourceIdentifierConverter.ReadJson(
                            reader,
                            objectType,
                            existingValue,
                            serializer);
                        break;
                    case PropertyNames.Links:
                    case PropertyNames.Meta:
                        isValid = true;
                        break;
                    default:
                        break;
                }
            }

            if (!isValid)
            {
                var path = (reader as ForkableJsonReader)?.FullPath ?? reader.Path;
                throw new JsonApiFormatException(path,
                    $"Expected to find one of links, data or meta on relationship object",
                    "A relationship object MUST contain at least one of: links, data or meta");
            }

            return resourceObject;
        }
    }
}
