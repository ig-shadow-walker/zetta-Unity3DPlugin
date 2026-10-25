using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>The node kinds the server knows (pipeline-graph.ts NODE_KINDS), as string constants.</summary>
    public static class NodeKinds
    {
        public const string SeedPicture = "seed_picture";
        public const string SeedModel = "seed_model";
        public const string RemoveBackground = "remove_background";
        public const string AiEdit = "ai_edit";
        public const string DrawViews = "draw_views";
        public const string IsolateParts = "isolate_parts";
        public const string Generate3D = "generate_3d";
        public const string GenerateSmart3D = "generate_smart_3d";
        public const string GenerateSplat = "generate_splat";
        public const string Retopology = "retopology";
        public const string Segmentation = "segmentation";
        public const string SmartSegmentation = "smart_segmentation";
        public const string Texture = "texture";
        public const string SmartUv = "smart_uv";
        public const string Rigging = "rigging";
        public const string Export = "export";

        public static readonly string[] All =
        {
            SeedPicture, SeedModel, RemoveBackground, AiEdit, DrawViews, IsolateParts, Generate3D, GenerateSmart3D,
            GenerateSplat, Retopology, Segmentation, SmartSegmentation, Texture, SmartUv, Rigging, Export,
        };
    }

    /// <summary>What travels along a connection (the web's <c>PortType</c>).</summary>
    public static class PortTypes
    {
        public const string Picture = "picture";
        public const string Views = "views";
        public const string Mesh = "mesh";
        public const string Splat = "splat";
        public const string File = "file";

        public static readonly string[] All = { Picture, Views, Mesh, Splat, File };

        /// <summary>The colour each kind of connection wears on the canvas (node-visuals.ts PORT_COLOUR).</summary>
        public static Color Colour(string type) => type switch
        {
            Picture => Hex(0xe5, 0x7c, 0xf5),
            Views => Hex(0xf8, 0xc6, 0xff),
            Mesh => Hex(0x5e, 0xa4, 0xff),
            Splat => Hex(0x42, 0xd3, 0xba),
            File => Hex(0xa8, 0xde, 0x52),
            _ => Color.gray,
        };

        /// <summary>The same colour as the web writes it.</summary>
        public static string ColourHex(string type) => type switch
        {
            Picture => "#e57cf5",
            Views => "#f8c6ff",
            Mesh => "#5ea4ff",
            Splat => "#42d3ba",
            File => "#a8de52",
            _ => "#888888",
        };

        /// <summary>node-visuals.ts PORT_LABEL.</summary>
        public static string Label(string type) => type switch
        {
            Picture => "Picture",
            Views => "Views",
            Mesh => "Model",
            Splat => "Splat",
            File => "File",
            _ => type,
        };

        static Color Hex(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);
    }

    /// <summary>The palette's groups, in order.</summary>
    public enum NodeGroup
    {
        Seed,
        Picture,
        Make,
        Refine,
        Output,
    }

    /// <summary>One input socket.</summary>
    public sealed class InputPortSpec
    {
        public string Handle;
        public string Label;
        public IReadOnlyList<string> Accepts;
        public bool Required;

        public bool AcceptsType(string type) => type != null && Accepts.Contains(type);
    }

    /// <summary>One output socket. Isolate Parts also has one dynamic <c>part:&lt;key&gt;</c> output per part it learnt.</summary>
    public sealed class OutputPortSpec
    {
        public string Handle;
        public string Label;
        public string Type;
        /// <summary>A <c>part:&lt;key&gt;</c> output learnt from a run (Isolate Parts' settings.knownParts).</summary>
        public bool Dynamic;
    }

    /// <summary>What one kind of node is (the web's NodeSpec, src/lib/pipelines.ts NODE_SPECS).</summary>
    public sealed class NodeSpec
    {
        public string Kind;
        public string Label;
        /// <summary>One line for the palette.</summary>
        public string Blurb;
        public NodeGroup Group;
        public IReadOnlyList<InputPortSpec> Inputs;
        public IReadOnlyList<OutputPortSpec> Outputs;
        /// <summary>A 3D job — queued, charged in 3D credits, and watched. Otherwise the step is instant (and free of 3D credits).</summary>
        public bool ThreeD;
        /// <summary>Shown in the web palette.</summary>
        public bool Offered;
        /// <summary>
        /// Out of the Unity plugin's scope (Gaussian splats, the legacy Smart segmentation node, Export). A graph that
        /// has one still loads, shows and round-trips untouched — the node is just not offered, edited or rebuilt here.
        /// </summary>
        public bool NotAvailableInUnity;
        public bool EarlyAccess;

        /// <summary>Offered in the Unity palette (and to the Unity agent).</summary>
        public bool OfferedInUnity => Offered && !NotAvailableInUnity;

        /// <summary>Whether the step's tool has an engine to choose — the node must name one.</summary>
        public bool EngineChoice => NodeCatalog.EngineChoice.Contains(Kind);

        public InputPortSpec Input(string handle) => Inputs.FirstOrDefault(i => i.Handle == handle);
    }

    /// <summary>
    /// The node catalogue — a port of the web's NODE_SPECS (labels, blurbs, groups, sockets) plus the Unity scope.
    /// The server's own rules are Zetta3D-Backend src/pipelines/pipeline-graph.ts; change one, change the others.
    /// </summary>
    public static class NodeCatalog
    {
        public const string PartHandlePrefix = "part:";

        /// <summary>The steps whose Studio tool has an engine to choose (ENGINE_CHOICE).</summary>
        public static readonly HashSet<string> EngineChoice = new HashSet<string>
        {
            NodeKinds.Generate3D, NodeKinds.GenerateSmart3D, NodeKinds.GenerateSplat, NodeKinds.Retopology,
        };

        static InputPortSpec[] PictureIn => new[] { new InputPortSpec { Handle = "in", Label = "Picture", Accepts = new[] { PortTypes.Picture }, Required = true } };
        static InputPortSpec[] MeshIn => new[] { new InputPortSpec { Handle = "in", Label = "Model", Accepts = new[] { PortTypes.Mesh }, Required = true } };
        static InputPortSpec[] PictureOrViewsIn => new[] { new InputPortSpec { Handle = "in", Label = "Picture or views", Accepts = new[] { PortTypes.Picture, PortTypes.Views }, Required = true } };
        static OutputPortSpec[] Out(string label, string type) => new[] { new OutputPortSpec { Handle = "out", Label = label, Type = type } };
        static OutputPortSpec[] MeshOut => Out("Model", PortTypes.Mesh);
        static OutputPortSpec[] PictureOut => Out("Picture", PortTypes.Picture);

        static readonly Dictionary<string, NodeSpec> Specs = new[]
        {
            new NodeSpec { Kind = NodeKinds.SeedPicture, Label = "Picture seed", Blurb = "The picture the pipeline starts from — upload one, pick one, or make one.", Group = NodeGroup.Seed, Inputs = new InputPortSpec[0], Outputs = PictureOut, Offered = true },
            new NodeSpec { Kind = NodeKinds.SeedModel, Label = "Model seed", Blurb = "A model from your library, or one you upload.", Group = NodeGroup.Seed, Inputs = new InputPortSpec[0], Outputs = MeshOut, Offered = true },
            new NodeSpec { Kind = NodeKinds.RemoveBackground, Label = "Remove background", Blurb = "Cuts the subject out of its background. Free.", Group = NodeGroup.Picture, Inputs = PictureIn, Outputs = PictureOut, Offered = true },
            new NodeSpec { Kind = NodeKinds.AiEdit, Label = "AI edit", Blurb = "Changes the picture by an instruction.", Group = NodeGroup.Picture, Inputs = PictureIn, Outputs = PictureOut, Offered = true },
            new NodeSpec { Kind = NodeKinds.DrawViews, Label = "Draw views", Blurb = "Draws the back and sides from one picture, for a multi-view generation.", Group = NodeGroup.Picture, Inputs = PictureIn, Outputs = Out("Views", PortTypes.Views), Offered = true },
            new NodeSpec
            {
                Kind = NodeKinds.IsolateParts, Label = "Isolate parts", Blurb = "Splits a picture into its parts — the character, the weapon, the shield — each on its own.",
                Group = NodeGroup.Picture, Inputs = PictureIn, Offered = true,
                Outputs = new[]
                {
                    new OutputPortSpec { Handle = "character", Label = "Character", Type = PortTypes.Picture },
                    new OutputPortSpec { Handle = "each", Label = "Each part", Type = PortTypes.Picture },
                },
            },
            new NodeSpec { Kind = NodeKinds.Generate3D, Label = "Generate 3D", Blurb = "A model from a picture or from several views. Zetta N1 or N2.5.", Group = NodeGroup.Make, Inputs = PictureOrViewsIn, Outputs = MeshOut, ThreeD = true, Offered = true },
            new NodeSpec { Kind = NodeKinds.GenerateSmart3D, Label = "Generate Smart 3D", Blurb = "Clean topology from a picture or views. Zetta Np1 or Np1.5.", Group = NodeGroup.Make, Inputs = PictureOrViewsIn, Outputs = MeshOut, ThreeD = true, Offered = true },
            new NodeSpec { Kind = NodeKinds.GenerateSplat, Label = "Generate Splat", Blurb = "A splat object or environment from a picture.", Group = NodeGroup.Make, Inputs = PictureIn, Outputs = Out("Splat", PortTypes.Splat), ThreeD = true, Offered = false, EarlyAccess = true, NotAvailableInUnity = true },
            new NodeSpec { Kind = NodeKinds.Retopology, Label = "Retopology", Blurb = "A clean mesh at the polygon budget you set.", Group = NodeGroup.Refine, Inputs = MeshIn, Outputs = MeshOut, ThreeD = true, Offered = true },
            new NodeSpec { Kind = NodeKinds.Segmentation, Label = "Segmentation", Blurb = "Splits the model into parts — AI or Smart.", Group = NodeGroup.Refine, Inputs = MeshIn, Outputs = MeshOut, ThreeD = true, Offered = true },
            new NodeSpec { Kind = NodeKinds.SmartSegmentation, Label = "Smart segmentation", Blurb = "Splits the model into named parts.", Group = NodeGroup.Refine, Inputs = MeshIn, Outputs = MeshOut, ThreeD = true, Offered = false, NotAvailableInUnity = true },
            new NodeSpec { Kind = NodeKinds.Texture, Label = "AI texturing", Blurb = "New textures, from a reference picture or a description.", Group = NodeGroup.Refine, Inputs = MeshIn, Outputs = MeshOut, ThreeD = true, Offered = true },
            new NodeSpec { Kind = NodeKinds.SmartUv, Label = "Smart UV", Blurb = "A new UV layout, textures re-baked onto it. Up to 80,000 triangles.", Group = NodeGroup.Refine, Inputs = MeshIn, Outputs = MeshOut, ThreeD = true, Offered = true },
            new NodeSpec { Kind = NodeKinds.Rigging, Label = "Auto rig", Blurb = "A skeleton, skinned, ready to animate.", Group = NodeGroup.Refine, Inputs = MeshIn, Outputs = MeshOut, ThreeD = true, Offered = true },
            new NodeSpec { Kind = NodeKinds.Export, Label = "Export", Blurb = "GLB, FBX, OBJ or STL. Free.", Group = NodeGroup.Output, Inputs = MeshIn, Outputs = Out("File", PortTypes.File), Offered = false, NotAvailableInUnity = true },
        }.ToDictionary(s => s.Kind);

        /// <summary>Every known kind, in the server's order.</summary>
        public static IEnumerable<NodeSpec> All => NodeKinds.All.Select(k => Specs[k]);

        /// <summary>What the Unity palette offers, in order.</summary>
        public static IEnumerable<NodeSpec> OfferedInUnity => All.Where(s => s.OfferedInUnity);

        /// <summary>The groups and their palette titles (NODE_GROUPS).</summary>
        public static readonly IReadOnlyList<(NodeGroup group, string label)> Groups = new[]
        {
            (NodeGroup.Seed, "Seed"), (NodeGroup.Picture, "Picture"), (NodeGroup.Make, "Make"), (NodeGroup.Refine, "Refine"), (NodeGroup.Output, "Output"),
        };

        /// <summary>The spec of a kind, or null for a kind this version does not know.</summary>
        public static NodeSpec Spec(string kind) => kind != null && Specs.TryGetValue(kind, out var spec) ? spec : null;

        public static bool IsKnown(string kind) => kind != null && Specs.ContainsKey(kind);

        public static bool IsSeed(string kind) => kind == NodeKinds.SeedPicture || kind == NodeKinds.SeedModel;

        public static bool IsThreeD(string kind) => Spec(kind)?.ThreeD == true;

        /// <summary>Out of the Unity plugin's scope — or unknown to it. Such a node is shown and kept, never edited.</summary>
        public static bool NotAvailableInUnity(string kind) => Spec(kind)?.NotAvailableInUnity ?? true;

        /// <summary>The parts an Isolate Parts node has learnt from its runs (settings.knownParts), each with its own output.</summary>
        public static List<(string key, string label)> KnownParts(PipelineNode node)
        {
            var list = new List<(string, string)>();
            if (node?.Settings?["knownParts"] is JArray parts)
                foreach (var part in parts.OfType<JObject>())
                    if (part["key"]?.Type == JTokenType.String && part["label"]?.Type == JTokenType.String)
                        list.Add((part["key"].ToString(), part["label"].ToString()));
            return list;
        }

        /// <summary>
        /// A node's outputs as the canvas shows them: the fixed ones, then — for Isolate Parts — one
        /// <c>part:&lt;key&gt;</c> output per part it has learnt. An unknown kind has none.
        /// </summary>
        public static List<OutputPortSpec> OutputsOf(PipelineNode node)
        {
            var spec = Spec(node?.Kind);
            if (spec == null) return new List<OutputPortSpec>();
            var outputs = spec.Outputs.ToList();
            if (node.Kind == NodeKinds.IsolateParts)
                outputs.AddRange(KnownParts(node).Select(p => new OutputPortSpec
                {
                    Handle = PartHandlePrefix + p.key, Label = p.label, Type = PortTypes.Picture, Dynamic = true,
                }));
            return outputs;
        }

        /// <summary>What an output handle carries, or null for one the node does not have (outputType).</summary>
        public static string OutputType(string kind, string handle)
        {
            var spec = Spec(kind);
            if (spec == null || handle == null) return null;
            var fixedPort = spec.Outputs.FirstOrDefault(o => o.Handle == handle);
            if (fixedPort != null) return fixedPort.Type;
            if (kind == NodeKinds.IsolateParts && handle.StartsWith(PartHandlePrefix, StringComparison.Ordinal)) return PortTypes.Picture;
            return null;
        }

        /// <summary>An input socket of a kind, or null (inputPort).</summary>
        public static InputPortSpec InputPort(string kind, string handle) => Spec(kind)?.Input(handle);

        /// <summary>The kind's label, or the raw kind when unknown.</summary>
        public static string Label(string kind) => Spec(kind)?.Label ?? kind;
    }
}
