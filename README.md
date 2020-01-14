# JsonApiSerializer

The JsonApiSerializer provides configurationless serializing and deserializing objects into the [json:api](http://jsonapi.org) format.

It is implemented as an `JsonSerializerSettings` for [Json.Net](http://www.newtonsoft.com/json) with usage being the standard [Json.Net](http://www.newtonsoft.com/json) methods passing in a `JsonApiSerializerSettings`

```csharp
//To serialize a POCO in json:api format
string json = JsonConvert.SerializeObject(articles, new JsonApiSerializerSettings());

//To deserialize to a POCO from json:api format
Article[] articles = JsonConvert.DeserializeObject<Article[]>(json, new JsonApiSerializerSettings());
```

[![NuGet version](https://badge.fury.io/nu/JsonApiSerializer.svg)](https://badge.fury.io/nu/JsonApiSerializer)

## Example

Given an object model like:

```csharp
public class Article
{
    public string Id { get; set; }

    public string Title { get; set; }

    public Person Author { get; set; }

    public List<Comment> Comments { get; set; }
}

public class Comment
{
    public string Id { get; set; }

    public string Body { get; set; }

    public Person Author { get; set; }
}

public class Person
{
    public string Id { get; set; }

    [JsonProperty(propertyName: "first-name")] //uses standard Json.NET attributes to control serialization
    public string FirstName { get; set; }

    [JsonProperty(propertyName: "last-name")]
    public string LastName { get; set; }

    public string Twitter { get; set; }
}
```

### Deserialization

and json:api content that look something like

```json
{
  "data": [{
    "type": "articles",
    "id": "1",
    "attributes": {
        "title": "JSON API paints my bikeshed!"
    },
    "relationships": {
      "author": {
        "data": { "type": "people", "id": "9" }
      },
      "comments": {
        "data": [
          { "type": "comments", "id": "5" },
          { "type": "comments", "id": "12" }
        ]
      }
    }
  }],
  "included": [{
    "type": "people",
    "id": "9",
    "attributes": {
      "first-name": "Dan",
      "last-name": "Gebhardt",
      "twitter": "dgeb"
    },
  }, {
    "type": "comments",
    "id": "5",
    "attributes": {
      "body": "First!"
    },
    "relationships": {
      "author": {
        "data": { "type": "people", "id": "2" }
      }
    },
  }, {
    "type": "comments",
    "id": "12",
    "attributes": {
      "body": "I like XML better"
    },
    "relationships": {
      "author": {
        "data": { "type": "people", "id": "9" }
      }
    },
  }]
}
```

We can deserialize with 

```csharp
Article[] articles = JsonConvert.DeserializeObject<Article[]>(json, new JsonApiSerializerSettings());
```

### Serialization

We can also generate the JSON from our object model
```csharp

var author = new Person
{
    Id = "9",
    FirstName = "Dan",
    LastName = "Gebhardt",
    Twitter = "dgeb",
};

var articles = new[] {
    new Article
    {
        Id = "1",
        Title = "JSON API paints my bikeshed!",
        Author = author,
        Comments = new List<Comment>
        {
            new Comment
            {
                Id = "5",
                Body = "First!",
                Author = new Person
                {
                    Id = "2"
                },
            },
            new Comment
            {
                Id = "12",
                Body = "I like XML better",
                Author = author,
            }
        }
    }
};

//will produce the same json:api json value
string json = JsonConvert.SerializeObject(articles, new JsonApiSerializerSettings());

```

## Extracting more properties
json:api allows for additional information to be stored at objects and relationships, We provide some helper classes that allows you to access these properties.

### DocumentRoot

`DocumentRoot<TData>` allows you to get and set json:api values at the root document level

```json
{
	"jsonapi": {
		"version":"1.0"
	},
	"links": {
		"self": "http://example.com/articles",
	},
	"meta": {
		"created": "2017-04-02T23:28:35"
	},
	"data": [{
  		"id" : "1",
		"type": "articles",
		"attributes": {
	  		"title": "document root example"
		}
	}]
}
```

```csharp
DocumentRoot<Article[]> articlesRoot = JsonConvert.DeserializeObject<DocumentRoot<Article[]>>(json, new JsonApiSerializerSettings());
Assert.Equal("1.0" articlesRoot.JsonApi.Version);
Assert.Equal("http://example.com/articles", articlesRoot.Links["self"].Href);
Assert.Equal("2017-04-02T23:28:35", articlesRoot.Meta["created"]);
```

### Relationships

`Relationship<TData>` can be used in an object to get and set additional json:api around relationships such as links or meta

```json
{
	"data": {
		"type": "articles",
		"id": "1",
		"attributes": {
			"title": "JSON API paints my bikeshed!"
		},
		"relationships": {
			"author": {
				"links": {
				  "self": "http://example.com/articles/1/relationships/author",
				  "related": "http://example.com/articles/1/author"
				},
				"data": { "type": "people", "id": "9" }
			}
		}
	}
}
```

```csharp
public class Article
{
    public string Id { get; set; }

    public string Title { get; set; }

    public Relationship<Person> Author { get; set; }
}
```

```csharp
Article[] article = JsonConvert.DeserializeObject<Article>(json, new JsonApiSerializerSettings());
Assert.Equal("http://example.com/articles/1/relationships/author", article.Author.links["self"].Href);
Assert.Equal("http://example.com/articles/1/author", article.Author.links["related"].Href);
```

### Resource Identifiers

json:api specification allows defining metadata at the [resource identifier](https://jsonapi.org/format/#document-resource-identifier-objects) level. By default this resource identifier `meta` is folded into the object's `meta` (if the field is present).

However if you have distinct resource identifier `meta` and resource object `meta` you may define a relationship of type `IResourceIdentifier<T>`. This object will populate the `meta` property and place the resource object details within `Value` field.

```json
{
	"data": {
		"type": "articles",
		"id": "1",
		"attributes": {
			"title": "JSON API paints my bikeshed!"
		},
		"relationships": {
			"author": {
				"meta": {
				  "verified": true,
				},
				"data": { "type": "people", "id": "9" }
			}
		}
	},
	"included": [{
		"type": "people",
		"id": "9",
		"attributes": {
		  "first-name": "Dan",
		  "last-name": "Gebhardt",
		  "twitter": "dgeb"
		},
		"meta": {
			"verified" : false
		}
	}]
}
```

```csharp
public class Article
{
    public string Id { get; set; }

    public string Title { get; set; }

    public ResourceIdentifier<Person> Author { get; set; }
}
```

```csharp
Article[] article = JsonConvert.DeserializeObject<Article[]>(json, new JsonApiSerializerSettings());

Assert.Equal(true, article.Author.meta["verified"]); //resource identifier meta
Assert.Equal(true, article.Author.Value.meta["verified"]); //object meta
```

### Links

`Link` can be used to store link values. json:api supports links as either a string or as an object. JsonApiSerializer normalizes this behaviour so in the object model they are always an object.

A `Links` class is also provided to store a dictionary of links that is typically stored on json:api objects

### Types

If you dont specify a type property on your object model JsonApiSerializer will use the class name. If you want to modify this behaviour it is as simple as putting a `Type` property on a class

```csharp
public class Person
{
	public string Type { get; set; } = "people"; //sets type to "people"

	public string Id { get; set; }

	[JsonProperty(propertyName: "first-name")]
	public string FirstName { get; set; }

	[JsonProperty(propertyName: "last-name")]
	public string LastName { get; set; }

	public string Twitter { get; set; }
}
```

### Determining relationship objects
By default any class with an "Id" is considered a Resource Object, and it will be treated as a relationship during serialization and deserialization.

This can be overrided during initialization by providing your own `JsonConverter` (it is strongly recommneded you extend `ResourceObjectConverter`) when you create the `JsonApiSerializerSettings`. Your custom 'JsonConverter can override the `CanConvert(Type type)' method.

```csharp
var settings = new JsonApiSerializerSettings(new MyOwnJsonSerializer())
Article[] articles = JsonConvert.DeserializeObject<Article[]>(json, settings);
```

### Determine object type during deserialization
The default behaviour is to assume that the object type declared on your property is the type that will be created during deserialization. However there are times where you may want the property to be declared as an interface or abstract class and then determine the actual type to instantiate during deserialization. 

This can be achieved by creating a custom `JsonConvertor` extending from `ResoruceObjectConvertor` and overriding `CreateObject`. You can then provide custom logic in creating the object, typically this would be done by examing hte json:api type.

```csharp
    public class MyTypeDeterminingResourceObjectConvertor : ResourceObjectConverter
    {
        protected override object CreateObject(Type objectType, string jsonapiType, JsonSerializer serializer)
        {
            switch (jsonapiType)
            {
                case "vip-person":
                    return new PersonVIP();
                case "person":
                    return new Person();
                default:
                    return base.CreateObject(objectType, jsonapiType, serializer);
            }
        }
    }
```

This convertor can be added into the system in a number of ways 

1. Adding it globally to all resource objects by specifiying during the creation of the `JsonApiSerializerSettings`.

**Note that the custom converter is saved via the `ContractResolver` property. Therefore, if you also override the `JsonApiContractResolver` and set it as the `JsonApiSerializerSettings.ContractResolver`, make sure to pass in your custom `JsonConverter` to it's constructor.**
```csharp
var settings = new JsonApiSerializerSettings(new MyTypeDeterminingResourceObjectConvertor())
Article[] articles = JsonConvert.DeserializeObject<Article[]>(json, settings);
```
2. Adding to everything that matches the `CanConvert` method on your customer converter. If doing this you would want to override the `CanConvert` method so it only targets object types you want to use the new convertor.
```csharp
var settings = new JsonApiSerializerSettings()
settings.Converters.Add(new MyTypeDeterminingResourceObjectConvertor())
Article[] articles = JsonConvert.DeserializeObject<Article[]>(json, settings);
```
3. Add to your model by annotating it with a `JsonConvertorAttribute`. This behaviour is identical to usage by json.net
```csharp
[JsonConvertor(typeof(MyTypeDeterminingResourceObjectConvertor))]
public IPerson Author {get; set;}
```

### Integrating with Microsoft.AspNetCore.Mvc

You can configure json:api to be the default serialization for your MVC site by reconfiguring the `JsonInputFormatter` and `JsonOutputFormatter` to use the `JsonApiSerializerSettings`

```csharp
public class Startup
{

	// ...

    public void ConfigureServices(IServiceCollection services)
    {
        // ...

        var sp = services.BuildServiceProvider();
        var logger = sp.GetService<ILoggerFactory>();
        var objectPoolProvider = sp.GetService<ObjectPoolProvider>();

        services.AddMvc(opt => {
            var serializerSettings = new JsonApiSerializerSettings();

            var jsonApiFormatter = new JsonOutputFormatter(serializerSettings, ArrayPool<Char>.Shared);
            opt.OutputFormatters.RemoveType<JsonOutputFormatter>();
            opt.OutputFormatters.Insert(0, jsonApiFormatter);

            var jsonApiInputFormatter = new JsonInputFormatter(logger.CreateLogger<JsonInputFormatter>(), serializerSettings, ArrayPool<Char>.Shared, objectPoolProvider);
            opt.InputFormatters.RemoveType<JsonInputFormatter>();
            opt.InputFormatters.Insert(0, jsonApiInputFormatter);

        });
    }
}
```


