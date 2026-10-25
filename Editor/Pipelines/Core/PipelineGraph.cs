using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>
    /// A 3D pipeline's recipe: nodes that are the Studio's own tools, wired from ONE seed. The same JSON the
    /// web editor and the server keep (Zetta3D-Backend src/pipelines/pipeline-graph.ts,
    /// Zetta3D-Frontend src/lib/pipelines.ts).
    ///
    /// LOSSLESS: every property this class does not model — on the graph, a node or an edge — is kept in
    /// <c>Extra</c> and written back as it came, so a graph saved by a newer web editor survives a round
    /// trip through Unity. Unknown node kinds load too (see <see cref="NodeCatalog.IsKnown"/>).
    ///
    /// The operations in this namespace treat a graph as a value: they <see cref="Clone"/> and return a new
    /// one rather than changing the one they were given (see <see cref="GraphEditing"/>).
    /// </summary>
    [JsonConverter(typeof(PipelineGraphConverter))]
    public sealed class PipelineGraph
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public List<PipelineNode> Nodes = new List<PipelineNode>();
        public List<PipelineEdge> Edges = new List<PipelineEdge>();
        /// <summary>Properties of the graph this class does not model, kept verbatim.</summary>
        public JObject Extra = new JObject();

        public static PipelineGraph Empty() => new PipelineGraph();

        public PipelineNode Node(string id) => id == null ? null : Nodes.FirstOrDefault(n => n.Id == id);
        public PipelineEdge Edge(string id) => id == null ? null : Edges.FirstOrDefault(e => e.Id == id);

        /// <summary>The seed node (the first, if a broken graph has several), or null.</summary>
        public PipelineNode Seed => Nodes.FirstOrDefault(n => NodeCatalog.IsSeed(n.Kind));

        public PipelineGraph Clone() => FromJson(ToJson());

        public JObject ToJson()
        {
            var json = (JObject)Extra.DeepClone();
            json["version"] = Version;
            json["nodes"] = new JArray(Nodes.Select(n => n.ToJson()));
            json["edges"] = new JArray(Edges.Select(e => e.ToJson()));
            return json;
        }

        /// <summary>Reads a graph as the web editor would: missing or malformed parts become empty, never an exception.</summary>
        public static PipelineGraph FromJson(JToken token)
        {
            var graph = new PipelineGraph();
            if (!(token is JObject json)) return graph;
            foreach (var pair in json)
            {
                switch (pair.Key)
                {
                    case "version":
                        graph.Version = pair.Value?.Type == JTokenType.Integer ? pair.Value.Value<int>() : CurrentVersion;
                        break;
                    case "nodes":
                        if (pair.Value is JArray nodes)
                            graph.Nodes = nodes.OfType<JObject>().Select(PipelineNode.FromJson).Where(n => n != null).ToList();
                        break;
                    case "edges":
                        if (pair.Value is JArray edges)
                            graph.Edges = edges.OfType<JObject>().Select((e, i) => PipelineEdge.FromJson(e, i)).Where(e => e != null).ToList();
                        break;
                    default:
                        graph.Extra[pair.Key] = pair.Value?.DeepClone();
                        break;
                }
            }
            return graph;
        }

        public static PipelineGraph Parse(string json) => FromJson(string.IsNullOrWhiteSpace(json) ? null : JToken.Parse(json));

        public override string ToString() => ToJson().ToString(Formatting.None);
    }

    /// <summary>A position on the canvas, in the web editor's pixels.</summary>
    public struct NodePosition
    {
        public double X;
        public double Y;

        public NodePosition(double x, double y)
        {
            X = x;
            Y = y;
        }

        public override string ToString() => $"({X}, {Y})";
    }

    /// <summary>One step of a pipeline.</summary>
    public sealed class PipelineNode
    {
        public string Id;
        /// <summary>One of <see cref="NodeKinds"/> — or a kind this version does not know, kept as it came.</summary>
        public string Kind;
        public NodePosition Position;
        /// <summary>The node's form state, web-shaped (the Studio form's own value). Null when the graph has none.</summary>
        public JObject Settings;
        /// <summary>What the form builds for the API, minus its inputs — the only thing the server reads.</summary>
        public JObject Request;
        /// <summary>A name the user gave it, shown instead of the kind's label. Null when unnamed.</summary>
        public string Label;
        /// <summary>Properties of the node this class does not model, kept verbatim.</summary>
        public JObject Extra = new JObject();

        /// <summary>The name to show: the user's label, else the catalogue's.</summary>
        public string DisplayName => !string.IsNullOrEmpty(Label) ? Label : NodeCatalog.Spec(Kind)?.Label ?? Kind;

        public PipelineNode Clone() => FromJson(ToJson());

        public JObject ToJson()
        {
            var json = (JObject)Extra.DeepClone();
            json["id"] = Id;
            json["kind"] = Kind;
            json["position"] = new JObject { ["x"] = Number(Position.X), ["y"] = Number(Position.Y) };
            if (Settings != null) json["settings"] = Settings.DeepClone();
            if (Request != null) json["request"] = Request.DeepClone();
            if (Label != null) json["label"] = Label;
            return json;
        }

        /// <summary>Whole numbers stay integers in the JSON, as the web writes them.</summary>
        static JToken Number(double value) =>
            Math.Abs(value % 1) < double.Epsilon && Math.Abs(value) < long.MaxValue ? new JValue((long)value) : new JValue(value);

        public static PipelineNode FromJson(JObject json)
        {
            if (json == null || json["id"]?.Type != JTokenType.String) return null;
            var node = new PipelineNode();
            foreach (var pair in json)
            {
                switch (pair.Key)
                {
                    case "id":
                        node.Id = pair.Value.ToString();
                        break;
                    case "kind":
                        node.Kind = pair.Value?.Type == JTokenType.String ? pair.Value.ToString() : pair.Value?.ToString(Formatting.None);
                        break;
                    case "position":
                        node.Position = new NodePosition(Coordinate(pair.Value?["x"]), Coordinate(pair.Value?["y"]));
                        break;
                    case "settings":
                        if (pair.Value is JObject settings) node.Settings = (JObject)settings.DeepClone();
                        else node.Extra[pair.Key] = pair.Value?.DeepClone();
                        break;
                    case "request":
                        if (pair.Value is JObject request) node.Request = (JObject)request.DeepClone();
                        else node.Extra[pair.Key] = pair.Value?.DeepClone();
                        break;
                    case "label":
                        if (pair.Value?.Type == JTokenType.String) node.Label = pair.Value.ToString();
                        else node.Extra[pair.Key] = pair.Value?.DeepClone();
                        break;
                    default:
                        node.Extra[pair.Key] = pair.Value?.DeepClone();
                        break;
                }
            }
            return node;
        }

        static double Coordinate(JToken token)
        {
            if (token == null) return 0;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<double>();
            return double.TryParse(token.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
    }

    /// <summary>A connection from one node's output to another's input.</summary>
    public sealed class PipelineEdge
    {
        public string Id;
        public string Source;
        public string SourceHandle = "out";
        public string Target;
        public string TargetHandle = "in";
        /// <summary>Properties of the edge this class does not model (React Flow's own, say), kept verbatim.</summary>
        public JObject Extra = new JObject();

        public PipelineEdge Clone() => FromJson(ToJson(), 0);

        public JObject ToJson()
        {
            var json = (JObject)Extra.DeepClone();
            json["id"] = Id;
            json["source"] = Source;
            json["sourceHandle"] = SourceHandle;
            json["target"] = Target;
            json["targetHandle"] = TargetHandle;
            return json;
        }

        /// <summary>The server's parseGraph rules: source and target required; handles default to out / in.</summary>
        public static PipelineEdge FromJson(JObject json, int index)
        {
            if (json == null || json["source"]?.Type != JTokenType.String || json["target"]?.Type != JTokenType.String) return null;
            var edge = new PipelineEdge();
            foreach (var pair in json)
            {
                var text = pair.Value?.Type == JTokenType.String ? pair.Value.ToString() : null;
                switch (pair.Key)
                {
                    case "id" when text != null:
                        edge.Id = text;
                        break;
                    case "source":
                        edge.Source = text;
                        break;
                    case "target":
                        edge.Target = text;
                        break;
                    case "sourceHandle" when text != null:
                        edge.SourceHandle = text;
                        break;
                    case "targetHandle" when text != null:
                        edge.TargetHandle = text;
                        break;
                    default:
                        edge.Extra[pair.Key] = pair.Value?.DeepClone();
                        break;
                }
            }
            edge.Id ??= $"e{index}";
            return edge;
        }
    }

    /// <summary>Lets <see cref="PipelineGraph"/> sit inside API models read with <c>ToObject&lt;T&gt;()</c>.</summary>
    public sealed class PipelineGraphConverter : JsonConverter<PipelineGraph>
    {
        public override PipelineGraph ReadJson(JsonReader reader, Type objectType, PipelineGraph existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            return token.Type == JTokenType.Null ? null : PipelineGraph.FromJson(token);
        }

        public override void WriteJson(JsonWriter writer, PipelineGraph value, JsonSerializer serializer)
        {
            if (value == null) writer.WriteNull();
            else value.ToJson().WriteTo(writer);
        }
    }
}
