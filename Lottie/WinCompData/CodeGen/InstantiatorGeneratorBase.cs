// Copyright(c) Microsoft Corporation.All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using WinCompData.Mgcg;
using WinCompData.Sn;
using WinCompData.Tools;
using WinCompData.Wui;

namespace WinCompData.CodeGen
{
#if !WINDOWS_UWP
    public
#endif
    abstract class InstantiatorGeneratorBase
    {
        // The name of the field holding the singleton reusable ExpressionAnimation.
        const string c_singletonExpressionAnimationName = "_reusableExpressionAnimation";
        const string c_durationTicksFieldName = "c_durationTicks";
        readonly bool _setCommentProperties;
        readonly ObjectGraph<ObjectData> _objectGraph;
        // The subset of the object graph for which nodes will be generated.
        readonly ObjectData[] _canonicalNodes;
        readonly IStringifier _stringifier;
        readonly HashSet<(ObjectData, ObjectData)> _factoriesAlreadyCalled = new HashSet<(ObjectData, ObjectData)>();
        TimeSpan _compositionDuration;

        protected InstantiatorGeneratorBase(CompositionObject graphRoot, TimeSpan duration, bool setCommentProperties, IStringifier stringifier)
        {
            _compositionDuration = duration;
            _setCommentProperties = setCommentProperties;
            _stringifier = stringifier;

            // Build the object graph.
            _objectGraph = ObjectGraph<ObjectData>.FromCompositionObject(graphRoot, includeVertices: true);

            // Canonicalize the nodes.
            Canonicalizer.Canonicalize(_objectGraph, !setCommentProperties);

            // Filter out ExpressionAnimations that are unique. They will use a single instance that is reset on each use.
            var canonicals =
                from node in _objectGraph
                where !(node.Object is ExpressionAnimation) ||
                        node.NodesInGroup.Count() > 1
                select node.Canonical;

            // Filter out types for which we won't create objects:
            //  AnimationController is created implicitly.
            //  CompositionPropertySet is created implicitly.
            canonicals =
                (from node in canonicals
                 where node.Type != Graph.NodeType.CompositionObject ||
                     !(((CompositionObject)node.Object).Type == CompositionObjectType.AnimationController ||
                      ((CompositionObject)node.Object).Type == CompositionObjectType.CompositionPropertySet)
                 select node.Canonical).Distinct().ToArray();

            // Give names to each canonical node.
            SetCanonicalMethodNames(canonicals);

            // Give the root node a special name.
            var rootNode = NodeFor(graphRoot);
            rootNode.Name = "Root";

            // Save the canonical nodes, ordered by the name that was just set.
            _canonicalNodes = canonicals.OrderBy(node => node.Name).ToArray();

            // Force storage to be allocated for nodes that have multiple references to them.
            foreach (var node in canonicals)
            {
                if (FilteredCanonicalInRefs(node).Count() > 1)
                {
                    node.RequiresStorage = true;
                }
            }

            // Force inlining on CompositionPath nodes because they are always very simple.
            foreach (var node in canonicals.Where(node => node.Type == Graph.NodeType.CompositionPath))
            {
                if (node.CanonicalInRefs.Count() <= 1)
                {
                    var pathSourceFactoryCall = CallFactoryFromFor(node, ((CompositionPath)node.Object).Source);
                    node.ForceInline($"{New} CompositionPath({_stringifier.FactoryCall(pathSourceFactoryCall)})");
                }
            }

            // Ensure the root object has storage if it is referenced by anything else in the graph.
            // This is necessary because the root node is referenced from the instantiator entrypoint
            // but that isn't counted in the CanonicalInRefs.
            if (rootNode.CanonicalInRefs.Any())
            {
                rootNode.RequiresStorage = true;
            }
        }

        /// <summary>
        /// Writes the using namespace statements and includes at the top of the file.
        /// </summary>
        protected abstract void WritePreamble(CodeBuilder builder, bool requiresWin2d);

        /// <summary>
        /// Writes the start of the class.
        /// </summary>
        protected abstract void WriteClassStart(
            CodeBuilder builder,
            string className,
            Vector2 size,
            CompositionPropertySet progressPropertySet,
            TimeSpan duration);


        /// <summary>
        /// Writes the end of the class.
        /// </summary>
        protected abstract void WriteClassEnd(CodeBuilder builder, Visual rootVisual, string reusableExpressionAnimationField);

        /// <summary>
        /// Writes CanvasGeometery.Combination factory code.
        /// </summary>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryCombinationFactory(CodeBuilder builder, CanvasGeometry.Combination obj, string typeName, string fieldName);

        /// <summary>
        /// Writes CanvasGeometery.Ellipse factory code.
        /// </summary>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryEllipseFactory(CodeBuilder builder, CanvasGeometry.Ellipse obj, string typeName, string fieldName);

        /// <summary>
        /// Writes CanvasGeometery.Path factory code.
        /// </summary>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryPathFactory(CodeBuilder builder, CanvasGeometry.Path obj, string typeName, string fieldName);

        /// <summary>
        /// Writes CanvasGeometery.RoundedRectangle factory code.
        /// </summary>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryRoundedRectangleFactory(CodeBuilder builder, CanvasGeometry.RoundedRectangle obj, string typeName, string fieldName);

        /// <summary>
        /// Call this to generate the code. Returns a string containing the generated code.
        /// </summary>
        protected string GenerateCode(
            string className,
            Visual rootVisual,
            float width,
            float height,
            CompositionPropertySet progressPropertySet)
        {
            var codeBuilder = new CodeBuilder();

            // Write the auto-generated warning comment.
            codeBuilder.WriteLine("//------------------------------------------------------------------------------");
            codeBuilder.WriteLine("// <auto-generated>");
            codeBuilder.WriteLine("//     This code was generated by a tool.");
            codeBuilder.WriteLine("//");
            codeBuilder.WriteLine("//     Changes to this file may cause incorrect behavior and will be lost if");
            codeBuilder.WriteLine("//     the code is regenerated.");
            codeBuilder.WriteLine("// </auto-generated>");
            codeBuilder.WriteLine("//------------------------------------------------------------------------------");

            // Generate #includes and usings for namespaces.
            var requiresWin2D = _canonicalNodes.Where(n => n.RequiresWin2D).Any();

            WritePreamble(codeBuilder, requiresWin2D);

            WriteClassStart(codeBuilder, className, new Vector2(width, height), progressPropertySet, _compositionDuration);

            // Write fields for constant values.
            WriteField(codeBuilder, Const(_stringifier.Int64TypeName), $"{c_durationTicksFieldName} = {_stringifier.Int64(_compositionDuration.Ticks)}");

            // Write fields for each object that needs storage (i.e. objects that are 
            // referenced more than once).
            WriteField(codeBuilder, Readonly(_stringifier.ReferenceTypeName("Compositor")), "_c");
            WriteField(codeBuilder, Readonly(_stringifier.ReferenceTypeName("ExpressionAnimation")), c_singletonExpressionAnimationName);
            foreach (var node in _canonicalNodes)
            {
                if (node.RequiresStorage)
                {
                    // Generate a field for the storage.
                    WriteField(codeBuilder, _stringifier.ReferenceTypeName(node.TypeName), node.FieldName);
                }
            }
            codeBuilder.WriteLine();

            // Write methods for each node.
            foreach (var node in _canonicalNodes)
            {
                WriteCodeForNode(codeBuilder, node);
            }

            WriteClassEnd(codeBuilder, rootVisual, c_singletonExpressionAnimationName);
            return codeBuilder.ToString();
        }

        /// <summary>
        /// Returns the code to call the factory for the given object.
        /// </summary>
        protected string CallFactoryFor(object obj)
        {
            var node = NodeFor(obj);
            return node.FactoryCall();
        }

        // Returns the code to call the factory for the given node from the given node.
        string CallFactoryFromFor(ObjectData callerNode, ObjectData calleeNode)
        {
            // If the object does not have a higher preorder traversal position 
            // than the caller then the object will have already been created by
            // the time the caller code runs, so that object can be read directly 
            // out of the cache field.
            if (callerNode.PreorderPosition >= calleeNode.PreorderPosition)
            {
                Debug.Assert(calleeNode.RequiresStorage);
                return calleeNode.FieldName;
            }
            else if (calleeNode.RequiresStorage && _factoriesAlreadyCalled.Contains((callerNode, calleeNode)))
            {
                return calleeNode.FieldName;
            }
            else
            {
                // Keep track of the fact that the caller called the factory
                // already. If the caller asks for the factory twice and the factory
                // does not have a cache, then the caller was expected to store the
                // result in a local.
                // NOTE: currently there is no generated code that is known to hit this case,
                // so this is just here to ensure we find it if it happens.
                if (!_factoriesAlreadyCalled.Add((callerNode, calleeNode)))
                {
                    throw new InvalidOperationException();
                }
                return calleeNode.FactoryCall();
            }
        }

        // Returns the code to call the factory for the given object from the given node.
        string CallFactoryFromFor(ObjectData callerNode, object obj) => CallFactoryFromFor(callerNode, NodeFor(obj));

        // Returns the canonical node for the given object.
        ObjectData NodeFor(object obj) => _objectGraph[obj].Canonical;

        // Gets the CanonicalInRefs for node, ignoring those from ExpressionAnimations
        // that have a single instance because they are treated specially (they are initialized inline).
        IEnumerable<ObjectData> FilteredCanonicalInRefs(ObjectData node)
        {
            // Examine all of the inrefs to the node.
            foreach (var item in node.CanonicalInRefs)
            {
                // If the inref is from an ExpressionAnimation ...
                if (item.Object is ExpressionAnimation exprAnim)
                {
                    // ... is the animation shared?
                    if (item.NodesInGroup.Count() > 1)
                    {
                        yield return item;
                        continue;
                    }

                    // ... is the anmation animating a property on the current node or its property set.
                    bool isExpressionOnThisNode = false;

                    var compObject = node.Object as CompositionObject;
                    // Search the animators to find the animator for this ExpressionAnimation.
                    // It will be found iff the ExpressionAnimation is animating this node.
                    foreach (var animator in compObject.Animators.Concat(compObject.Properties.Animators))
                    {
                        if (animator.Animation is ExpressionAnimation animatorExpression &&
                            animatorExpression.Expression == exprAnim.Expression)
                        {
                            isExpressionOnThisNode = true;
                            break;
                        }
                    }

                    if (!isExpressionOnThisNode)
                    {
                        yield return item;
                    }
                }
                else
                {
                    yield return item;
                }
            }
        }

        void WriteField(CodeBuilder builder, string typeName, string fieldName)
        {
            builder.WriteLine($"{typeName} {fieldName};");
        }

        // Generates code for the given node. The code is written into the CodeBuilder on the node.
        void WriteCodeForNode(CodeBuilder builder, ObjectData node)
        {
            // Only generate if the node is not inlined into the caller.
            if (!node.Inlined)
            {
                switch (node.Type)
                {
                    case Graph.NodeType.CompositionObject:
                        GenerateObjectFactory(builder, (CompositionObject)node.Object, node);
                        break;
                    case Graph.NodeType.CompositionPath:
                        GenerateCompositionPathFactory(builder, (CompositionPath)node.Object, node);
                        break;
                    case Graph.NodeType.CanvasGeometry:
                        GenerateCanvasGeometryFactory(builder, (CanvasGeometry)node.Object, node);
                        return;
                    default:
                        throw new InvalidOperationException();
                }
            }
        }

        bool GenerateCanvasGeometryFactory(CodeBuilder builder, CanvasGeometry obj, ObjectData node)
        {
            switch (obj.Type)
            {
                case CanvasGeometry.GeometryType.Combination:
                    return GenerateCanvasGeometryCombinationFactory(builder, (CanvasGeometry.Combination)obj, node);
                case CanvasGeometry.GeometryType.Ellipse:
                    return GenerateCanvasGeometryEllipseFactory(builder, (CanvasGeometry.Ellipse)obj, node);
                case CanvasGeometry.GeometryType.Path:
                    return GenerateCanvasGeometryPathFactory(builder, (CanvasGeometry.Path)obj, node);
                case CanvasGeometry.GeometryType.RoundedRectangle:
                    return GenerateCanvasGeometryRoundedRectangleFactory(builder, (CanvasGeometry.RoundedRectangle)obj, node);
                default:
                    throw new InvalidOperationException();
            }
        }

        bool GenerateCanvasGeometryCombinationFactory(CodeBuilder builder, CanvasGeometry.Combination obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            // Call the subclass to write the body.
            WriteCanvasGeometryCombinationFactory(builder, obj, _stringifier.ReferenceTypeName(node.TypeName), node.FieldName);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCanvasGeometryEllipseFactory(CodeBuilder builder, CanvasGeometry.Ellipse obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            // Call the subclass to write the body.
            WriteCanvasGeometryEllipseFactory(builder, obj, _stringifier.ReferenceTypeName(node.TypeName), node.FieldName);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCanvasGeometryPathFactory(CodeBuilder builder, CanvasGeometry.Path obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            // Call the subclass to write the body.
            WriteCanvasGeometryPathFactory(builder, obj, _stringifier.ReferenceTypeName(node.TypeName), node.FieldName);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCanvasGeometryRoundedRectangleFactory(CodeBuilder builder, CanvasGeometry.RoundedRectangle obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            // Call the subclass to write the body.
            WriteCanvasGeometryRoundedRectangleFactory(builder, obj, _stringifier.ReferenceTypeName(node.TypeName), node.FieldName);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateObjectFactory(CodeBuilder builder, CompositionObject obj, ObjectData node)
        {
            switch (obj.Type)
            {
                case CompositionObjectType.AnimationController:
                    // Do not generate code for animation controllers. It is done inline in the CompositionObject initialization.
                    throw new InvalidOperationException();
                case CompositionObjectType.ColorKeyFrameAnimation:
                    return GenerateColorKeyFrameAnimationFactory(builder, (ColorKeyFrameAnimation)obj, node);
                case CompositionObjectType.CompositionColorBrush:
                    return GenerateCompositionColorBrushFactory(builder, (CompositionColorBrush)obj, node);
                case CompositionObjectType.CompositionContainerShape:
                    return GenerateContainerShapeFactory(builder, (CompositionContainerShape)obj, node);
                case CompositionObjectType.CompositionEllipseGeometry:
                    return GenerateCompositionEllipseGeometryFactory(builder, (CompositionEllipseGeometry)obj, node);
                case CompositionObjectType.CompositionPathGeometry:
                    return GenerateCompositionPathGeometryFactory(builder, (CompositionPathGeometry)obj, node);
                case CompositionObjectType.CompositionPropertySet:
                    // Do not generate code for property sets. It is done inline in the CompositionObject initialization.
                    return true;
                case CompositionObjectType.CompositionRectangleGeometry:
                    return GenerateCompositionRectangleGeometryFactory(builder, (CompositionRectangleGeometry)obj, node);
                case CompositionObjectType.CompositionRoundedRectangleGeometry:
                    return GenerateCompositionRoundedRectangleGeometryFactory(builder, (CompositionRoundedRectangleGeometry)obj, node);
                case CompositionObjectType.CompositionSpriteShape:
                    return GenerateSpriteShapeFactory(builder, (CompositionSpriteShape)obj, node);
                case CompositionObjectType.CompositionViewBox:
                    return GenerateCompositionViewBoxFactory(builder, (CompositionViewBox)obj, node);
                case CompositionObjectType.ContainerVisual:
                    return GenerateContainerVisualFactory(builder, (ContainerVisual)obj, node);
                case CompositionObjectType.CubicBezierEasingFunction:
                    return GenerateCubicBezierEasingFunctionFactory(builder, (CubicBezierEasingFunction)obj, node);
                case CompositionObjectType.ExpressionAnimation:
                    return GenerateExpressionAnimationFactory(builder, (ExpressionAnimation)obj, node);
                case CompositionObjectType.InsetClip:
                    return GenerateInsetClipFactory(builder, (InsetClip)obj, node);
                case CompositionObjectType.LinearEasingFunction:
                    return GenerateLinearEasingFunctionFactory(builder, (LinearEasingFunction)obj, node);
                case CompositionObjectType.PathKeyFrameAnimation:
                    return GeneratePathKeyFrameAnimationFactory(builder, (PathKeyFrameAnimation)obj, node);
                case CompositionObjectType.ScalarKeyFrameAnimation:
                    return GenerateScalarKeyFrameAnimationFactory(builder, (ScalarKeyFrameAnimation)obj, node);
                case CompositionObjectType.ShapeVisual:
                    return GenerateShapeVisualFactory(builder, (ShapeVisual)obj, node);
                case CompositionObjectType.StepEasingFunction:
                    return GenerateStepEasingFunctionFactory(builder, (StepEasingFunction)obj, node);
                case CompositionObjectType.Vector2KeyFrameAnimation:
                    return GenerateVector2KeyFrameAnimationFactory(builder, (Vector2KeyFrameAnimation)obj, node);
                case CompositionObjectType.Vector3KeyFrameAnimation:
                    return GenerateVector3KeyFrameAnimationFactory(builder, (Vector3KeyFrameAnimation)obj, node);
                default:
                    throw new InvalidOperationException();
            }
        }

        bool GenerateInsetClipFactory(CodeBuilder builder, InsetClip obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateInsetClip()");
            InitializeCompositionClip(builder, obj, node);
            if (obj.LeftInset != 0)
            {
                builder.WriteLine($"result{Deref}LeftInset = {Float(obj.LeftInset)}");
            }
            if (obj.RightInset != 0)
            {
                builder.WriteLine($"result{Deref}RightInset = {Float(obj.RightInset)}");
            }
            if (obj.TopInset != 0)
            {
                builder.WriteLine($"result{Deref}TopInset = {Float(obj.TopInset)}");
            }
            if (obj.BottomInset != 0)
            {
                builder.WriteLine($"result{Deref}BottomInset = {Float(obj.BottomInset)}");
            }
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateLinearEasingFunctionFactory(CodeBuilder builder, LinearEasingFunction obj, ObjectData node)
        {
            WriteSimpleObjectFactory(builder, node, $"_c{Deref}CreateLinearEasingFunction()");
            return true;
        }

        bool GenerateCubicBezierEasingFunctionFactory(CodeBuilder builder, CubicBezierEasingFunction obj, ObjectData node)
        {
            WriteSimpleObjectFactory(builder, node, $"_c{Deref}CreateCubicBezierEasingFunction({Vector2(obj.ControlPoint1)}, {Vector2(obj.ControlPoint2)})");
            return true;
        }

        bool GenerateStepEasingFunctionFactory(CodeBuilder builder, StepEasingFunction obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateStepEasingFunction()");
            if (obj.FinalStep != 1)
            {
                builder.WriteLine($"result{Deref}FinalStep = {Int(obj.FinalStep)};");
            }
            if (obj.InitialStep != 0)
            {
                builder.WriteLine($"result{Deref}InitialStep = {Int(obj.InitialStep)};");
            }
            if (obj.IsFinalStepSingleFrame)
            {
                builder.WriteLine($"result{Deref}IsFinalStepSingleFrame  = {Bool(obj.IsFinalStepSingleFrame)};");
            }
            if (obj.IsInitialStepSingleFrame)
            {
                builder.WriteLine($"result{Deref}IsInitialStepSingleFrame  = {Bool(obj.IsInitialStepSingleFrame)};");
            }
            if (obj.StepCount != 1)
            {
                builder.WriteLine($"result{Deref}StepCount = {Int(obj.StepCount)};");
            }
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateContainerVisualFactory(CodeBuilder builder, ContainerVisual obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateContainerVisual()");
            InitializeContainerVisual(builder, obj, node);
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateExpressionAnimationFactory(CodeBuilder builder, ExpressionAnimation obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateExpressionAnimation()");
            InitializeCompositionAnimation(builder, obj, node);
            builder.WriteLine($"result{Deref}Expression = {String(obj.Expression.Simplified.ToString())};");
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        void StartAnimations(CodeBuilder builder, CompositionObject obj, ObjectData node, string localName = "result", string animationNamePrefix = "")
        {
            var animators = obj.Properties.Animators.Concat(obj.Animators);
            bool controllerVariableAdded = false;
            foreach (var animator in animators)
            {
                // ExpressionAnimations are treated specially - a singleton
                // ExpressionAnimation is reset before each use, unless the animation
                // is shared.
                var anim = NodeFor(animator.Animation);
                if (anim.NodesInGroup.Count() == 1 && animator.Animation is ExpressionAnimation expressionAnimation)
                {
                    builder.WriteLine($"{c_singletonExpressionAnimationName}{Deref}ClearAllParameters();");
                    builder.WriteLine($"{c_singletonExpressionAnimationName}{Deref}Expression = {String(expressionAnimation.Expression.Simplified.ToString())};");
                    // If there is a Target set it. Note however that the Target isn't used for anything
                    // interesting in this scenario, and there is no way to reset the Target to an
                    // empty string (the Target API disallows empty). In reality, for all our uses
                    // the Target will not be set and it doesn't matter if it was set previously.
                    if (!string.IsNullOrWhiteSpace(expressionAnimation.Target))
                    {
                        builder.WriteLine($"{c_singletonExpressionAnimationName}{Deref}Target = {String(expressionAnimation.Target)};");
                    }
                    foreach (var rp in expressionAnimation.ReferenceParameters)
                    {
                        var referenceParamenterValueName = rp.Value == obj
                            ? localName
                            : CallFactoryFromFor(anim, rp.Value);
                        builder.WriteLine($"{c_singletonExpressionAnimationName}{Deref}SetReferenceParameter({String(rp.Key)}, {referenceParamenterValueName});");
                    }
                    builder.WriteLine($"{localName}{Deref}StartAnimation({String(animator.AnimatedProperty)}, {c_singletonExpressionAnimationName});");
                }
                else
                {
                    // KeyFrameAnimation or shared animation
                    var animationFactoryCall = CallFactoryFromFor(node, anim);
                    builder.WriteLine($"{localName}{Deref}StartAnimation({String(animator.AnimatedProperty)}, {animationFactoryCall});");
                }

                if (animator.Controller != null)
                {
                    if (!controllerVariableAdded)
                    {
                        // Declare and initialize the controller variable.
                        builder.WriteLine($"{Var} controller = {localName}{Deref}TryGetAnimationController({String(animator.AnimatedProperty)});");
                        controllerVariableAdded = true;
                    }
                    else
                    {
                        // Initialize the controller variable.
                        builder.WriteLine($"controller = {localName}{Deref}TryGetAnimationController({String(animator.AnimatedProperty)});");
                    }
                    // TODO - we always pause here, but really should only pause it if WinCompData does it. 
                    builder.WriteLine($"controller{Deref}Pause();");
                    // Recurse to start animations on the controller.
                    StartAnimations(builder, animator.Controller, node, "controller", "controller");
                }
            }
        }

        void InitializeCompositionObject(CodeBuilder builder, CompositionObject obj, ObjectData node, string localName = "result", string animationNamePrefix = "")
        {
            if (_setCommentProperties)
            {
                if (!string.IsNullOrWhiteSpace(obj.Comment))
                {
                    builder.WriteLine($"{localName}{Deref}Comment = {String(obj.Comment)};");
                }
            }

            var propertySet = obj.Properties;
            if (propertySet.PropertyNames.Any())
            {
                builder.WriteLine($"{Var} propertySet = {localName}{Deref}Properties;");
                foreach (var prop in propertySet.ScalarProperties)
                {
                    builder.WriteLine($"propertySet{Deref}InsertScalar({String(prop.Key)}, {Float(prop.Value)});");
                }

                foreach (var prop in propertySet.Vector2Properties)
                {
                    builder.WriteLine($"propertySet{Deref}InsertVector2({String(prop.Key)}, {Vector2(prop.Value)});");
                }
            }
        }

        void InitializeCompositionBrush(CodeBuilder builder, CompositionBrush obj, ObjectData node)
        {
            InitializeCompositionObject(builder, obj, node);
        }

        void InitializeVisual(CodeBuilder builder, Visual obj, ObjectData node)
        {
            InitializeCompositionObject(builder, obj, node);
            if (obj.CenterPoint.HasValue)
            {
                builder.WriteLine($"result{Deref}CenterPoint = {Vector3(obj.CenterPoint.Value)};");
            }
            if (obj.Clip != null)
            {
                builder.WriteLine($"result{Deref}Clip = {CallFactoryFromFor(node, obj.Clip)};");
            }
            if (obj.Offset.HasValue)
            {
                builder.WriteLine($"result{Deref}Offset = {Vector3(obj.Offset.Value)};");
            }
            if (obj.RotationAngleInDegrees.HasValue)
            {
                builder.WriteLine($"result{Deref}RotationAngleInDegrees = {Float(obj.RotationAngleInDegrees.Value)};");
            }
            if (obj.Scale.HasValue)
            {
                builder.WriteLine($"result{Deref}Scale = {Vector3(obj.Scale.Value)};");
            }
            if (obj.Size.HasValue)
            {
                builder.WriteLine($"result{Deref}Size = {Vector2(obj.Size.Value)};");
            }
        }

        void InitializeCompositionClip(CodeBuilder builder, CompositionClip obj, ObjectData node)
        {
            InitializeCompositionObject(builder, obj, node);

            if (obj.CenterPoint.X != 0 || obj.CenterPoint.Y != 0)
            {
                builder.WriteLine($"result{Deref}CenterPoint = {Vector2(obj.CenterPoint)};");
            }
            if (obj.Scale.X != 1 || obj.Scale.Y != 1)
            {
                builder.WriteLine($"result{Deref}Scale = {Vector2(obj.Scale)};");
            }
        }

        void InitializeCompositionShape(CodeBuilder builder, CompositionShape obj, ObjectData node)
        {
            InitializeCompositionObject(builder, obj, node);

            if (obj.CenterPoint.HasValue)
            {
                builder.WriteLine($"result{Deref}CenterPoint = {Vector2(obj.CenterPoint.Value)};");
            }
            if (obj.Offset != null)
            {
                builder.WriteLine($"result{Deref}Offset = {Vector2(obj.Offset.Value)};");
            }
            if (obj.RotationAngleInDegrees.HasValue)
            {
                builder.WriteLine($"result{Deref}RotationAngleInDegrees = {Float(obj.RotationAngleInDegrees.Value)};");
            }
            if (obj.Scale.HasValue)
            {
                builder.WriteLine($"result{Deref}Scale = {Vector2(obj.Scale.Value)};");
            }
        }

        void InitializeContainerVisual(CodeBuilder builder, ContainerVisual obj, ObjectData node)
        {
            InitializeVisual(builder, obj, node);

            if (obj.Children.Any())
            {
                builder.WriteLine($"{Var} children = result{Deref}Children;");
                foreach (var child in obj.Children)
                {
                    builder.WriteLine($"children{Deref}InsertAtTop({CallFactoryFromFor(node, child)});");
                }
            }
        }

        void InitializeCompositionGeometry(CodeBuilder builder, CompositionGeometry obj, ObjectData node)
        {
            InitializeCompositionObject(builder, obj, node);
            if (obj.TrimEnd != 1)
            {
                builder.WriteLine($"result{Deref}TrimEnd = {Float(obj.TrimEnd)};");
            }
            if (obj.TrimOffset != 0)
            {
                builder.WriteLine($"result{Deref}TrimOffset = {Float(obj.TrimOffset)};");
            }
            if (obj.TrimStart != 0)
            {
                builder.WriteLine($"result{Deref}TrimStart = {Float(obj.TrimStart)};");
            }
        }

        void InitializeCompositionAnimation(CodeBuilder builder, CompositionAnimation obj, ObjectData node)
        {
            InitializeCompositionAnimationWithParameters(
                builder,
                obj,
                node,
                obj.ReferenceParameters.Select(p => new KeyValuePair<string, string>(p.Key, $"{CallFactoryFromFor(node, p.Value)}")));
        }

        void InitializeCompositionAnimationWithParameters(CodeBuilder builder, CompositionAnimation obj, ObjectData node, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            InitializeCompositionObject(builder, obj, node);
            if (!string.IsNullOrWhiteSpace(obj.Target))
            {
                builder.WriteLine($"result{Deref}Target = {String(obj.Target)};");
            }
            foreach (var parameter in parameters)
            {
                builder.WriteLine($"result{Deref}SetReferenceParameter({String(parameter.Key)}, {parameter.Value});");
            }
        }

        void InitializeKeyFrameAnimation(CodeBuilder builder, KeyFrameAnimation_ obj, ObjectData node)
        {
            InitializeCompositionAnimation(builder, obj, node);
            builder.WriteLine($"result{Deref}Duration = {TimeSpan(obj.Duration)};");
        }

        bool GenerateColorKeyFrameAnimationFactory(CodeBuilder builder, ColorKeyFrameAnimation obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateColorKeyFrameAnimation()");
            InitializeKeyFrameAnimation(builder, obj, node);

            foreach (var kf in obj.KeyFrames)
            {
                switch (kf.Type)
                {
                    case KeyFrameAnimation<Color>.KeyFrameType.Expression:
                        var expressionKeyFrame = (KeyFrameAnimation<Color>.ExpressionKeyFrame)kf;
                        builder.WriteLine($"result{Deref}InsertExpressionKeyFrame({Float(kf.Progress)}, {String(expressionKeyFrame.Expression)}, {CallFactoryFromFor(node, kf.Easing)});");
                        break;
                    case KeyFrameAnimation<Color>.KeyFrameType.Value:
                        var valueKeyFrame = (KeyFrameAnimation<Color>.ValueKeyFrame)kf;
                        builder.WriteComment(valueKeyFrame.Value.Name);
                        builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {Color(valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateVector2KeyFrameAnimationFactory(CodeBuilder builder, Vector2KeyFrameAnimation obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateVector2KeyFrameAnimation()");
            InitializeKeyFrameAnimation(builder, obj, node);

            foreach (var kf in obj.KeyFrames)
            {
                switch (kf.Type)
                {
                    case KeyFrameAnimation<Vector2>.KeyFrameType.Expression:
                        var expressionKeyFrame = (KeyFrameAnimation<Vector2>.ExpressionKeyFrame)kf;
                        builder.WriteLine($"result{Deref}InsertExpressionKeyFrame({Float(kf.Progress)}, {String(expressionKeyFrame.Expression)}, {CallFactoryFromFor(node, kf.Easing)});");
                        break;
                    case KeyFrameAnimation<Vector2>.KeyFrameType.Value:
                        var valueKeyFrame = (KeyFrameAnimation<Vector2>.ValueKeyFrame)kf;
                        builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {Vector2(valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateVector3KeyFrameAnimationFactory(CodeBuilder builder, Vector3KeyFrameAnimation obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateVector3KeyFrameAnimation()");
            InitializeKeyFrameAnimation(builder, obj, node);

            foreach (var kf in obj.KeyFrames)
            {
                switch (kf.Type)
                {
                    case KeyFrameAnimation<Vector3>.KeyFrameType.Expression:
                        var expressionKeyFrame = (KeyFrameAnimation<Vector3>.ExpressionKeyFrame)kf;
                        builder.WriteLine($"result{Deref}InsertExpressionKeyFrame({Float(kf.Progress)}, {String(expressionKeyFrame.Expression)}, {CallFactoryFromFor(node, kf.Easing)});");
                        break;
                    case KeyFrameAnimation<Vector3>.KeyFrameType.Value:
                        var valueKeyFrame = (KeyFrameAnimation<Vector3>.ValueKeyFrame)kf;
                        builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {Vector3(valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GeneratePathKeyFrameAnimationFactory(CodeBuilder builder, PathKeyFrameAnimation obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreatePathKeyFrameAnimation()");
            InitializeKeyFrameAnimation(builder, obj, node);

            foreach (var kf in obj.KeyFrames)
            {
                var valueKeyFrame = (PathKeyFrameAnimation.ValueKeyFrame)kf;
                builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {CallFactoryFromFor(node, valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
            }
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }


        bool GenerateScalarKeyFrameAnimationFactory(CodeBuilder builder, ScalarKeyFrameAnimation obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateScalarKeyFrameAnimation()");
            InitializeKeyFrameAnimation(builder, obj, node);

            foreach (var kf in obj.KeyFrames)
            {
                switch (kf.Type)
                {
                    case KeyFrameAnimation<float>.KeyFrameType.Expression:
                        var expressionKeyFrame = (KeyFrameAnimation<float>.ExpressionKeyFrame)kf;
                        builder.WriteLine($"result{Deref}InsertExpressionKeyFrame({Float(kf.Progress)}, {String(expressionKeyFrame.Expression)}, {CallFactoryFromFor(node, kf.Easing)});");
                        break;
                    case KeyFrameAnimation<float>.KeyFrameType.Value:
                        var valueKeyFrame = (KeyFrameAnimation<float>.ValueKeyFrame)kf;
                        builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {Float(valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCompositionRectangleGeometryFactory(CodeBuilder builder, CompositionRectangleGeometry obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateRectangleGeometry()");
            InitializeCompositionGeometry(builder, obj, node);
            builder.WriteLine($"result{Deref}Size = {Vector2(obj.Size)};");
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCompositionRoundedRectangleGeometryFactory(CodeBuilder builder, CompositionRoundedRectangleGeometry obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateRoundedRectangleGeometry()");
            InitializeCompositionGeometry(builder, obj, node);
            builder.WriteLine($"result{Deref}CornerRadius = {Vector2(obj.CornerRadius)};");
            builder.WriteLine($"result{Deref}Size = {Vector2(obj.Size)};");
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCompositionEllipseGeometryFactory(CodeBuilder builder, CompositionEllipseGeometry obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateEllipseGeometry()");
            InitializeCompositionGeometry(builder, obj, node);
            if (obj.Center.X != 0 || obj.Center.Y != 0)
            {
                builder.WriteLine($"result{Deref}Center = {Vector2(obj.Center)};");
            }
            builder.WriteLine($"result{Deref}Radius = {Vector2(obj.Radius)};");
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCompositionPathGeometryFactory(CodeBuilder builder, CompositionPathGeometry obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            var path = NodeFor(obj.Path);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreatePathGeometry({CallFactoryFromFor(node, path)})");
            InitializeCompositionGeometry(builder, obj, node);
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCompositionColorBrushFactory(CodeBuilder builder, CompositionColorBrush obj, ObjectData node)
        {
            var createCallText = $"_c{Deref}CreateColorBrush({Color(obj.Color)})";
            if (obj.Animators.Any())
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, createCallText);
                InitializeCompositionBrush(builder, obj, node);
                StartAnimations(builder, obj, node);
                WriteObjectFactoryEnd(builder);
            }
            else
            {
                WriteSimpleObjectFactory(builder, node, createCallText);
            }
            return true;
        }

        bool GenerateShapeVisualFactory(CodeBuilder builder, ShapeVisual obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateShapeVisual()");
            InitializeContainerVisual(builder, obj, node);

            if (obj.Shapes.Any())
            {
                builder.WriteLine($"{Var} shapes = result{Deref}Shapes;");
                foreach (var shape in obj.Shapes)
                {
                    builder.WriteComment(shape.ShortDescription);
                    builder.WriteLine($"shapes{Deref}{IListAdd}({CallFactoryFromFor(node, shape)});");
                }
            }
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateContainerShapeFactory(CodeBuilder builder, CompositionContainerShape obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateContainerShape()");
            InitializeCompositionShape(builder, obj, node);
            if (obj.Shapes.Any())
            {
                builder.WriteLine($"{Var} shapes = result{Deref}Shapes;");
                foreach (var shape in obj.Shapes)
                {
                    builder.WriteLine($"shapes{Deref}{IListAdd}({CallFactoryFromFor(node, shape)});");
                }
            }
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateSpriteShapeFactory(CodeBuilder builder, CompositionSpriteShape obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateSpriteShape()");
            InitializeCompositionShape(builder, obj, node);

            if (obj.FillBrush != null)
            {
                builder.WriteLine($"result{Deref}FillBrush = {CallFactoryFromFor(node, obj.FillBrush)};");
            }
            if (obj.Geometry != null)
            {
                builder.WriteLine($"result{Deref}Geometry = {CallFactoryFromFor(node, obj.Geometry)};");
            }
            if (obj.IsStrokeNonScaling)
            {
                builder.WriteLine("result{Deref}IsStrokeNonScaling = true;");
            }
            if (obj.StrokeBrush != null)
            {
                builder.WriteLine($"result{Deref}StrokeBrush = {CallFactoryFromFor(node, obj.StrokeBrush)};");
            }
            if (obj.StrokeDashCap != CompositionStrokeCap.Flat)
            {
                builder.WriteLine($"result{Deref}StrokeDashCap = {StrokeCap(obj.StrokeDashCap)};");
            }
            if (obj.StrokeDashOffset != 0)
            {
                builder.WriteLine($"result{Deref}StrokeDashOffset = {Float(obj.StrokeDashOffset)};");
            }
            if (obj.StrokeDashArray.Count > 0)
            {
                builder.WriteLine($"{Var} strokeDashArray = result{Deref}StrokeDashArray;");
                foreach (var strokeDash in obj.StrokeDashArray)
                {
                    builder.WriteLine($"strokeDashArray{Deref}{IListAdd}({Float(strokeDash)});");
                }
            }
            if (obj.StrokeEndCap != CompositionStrokeCap.Flat)
            {
                builder.WriteLine($"result{Deref}StrokeEndCap = {StrokeCap(obj.StrokeEndCap)};");
            }
            if (obj.StrokeLineJoin != CompositionStrokeLineJoin.Miter)
            {
                builder.WriteLine($"result{Deref}StrokeLineJoin = {StrokeLineJoin(obj.StrokeLineJoin)};");
            }
            if (obj.StrokeStartCap != CompositionStrokeCap.Flat)
            {
                builder.WriteLine($"result{Deref}StrokeStartCap = {StrokeCap(obj.StrokeStartCap)};");
            }
            if (obj.StrokeMiterLimit != 1)
            {
                builder.WriteLine($"result{Deref}StrokeMiterLimit = {Float(obj.StrokeMiterLimit)};");
            }
            if (obj.StrokeThickness != 1)
            {
                builder.WriteLine($"result{Deref}StrokeThickness = {Float(obj.StrokeThickness)};");
            }
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCompositionViewBoxFactory(CodeBuilder builder, CompositionViewBox obj, ObjectData node)
        {
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"_c{Deref}CreateViewBox()");
            InitializeCompositionObject(builder, obj, node);
            builder.WriteLine($"result.Size = {Vector2(obj.Size)};");
            StartAnimations(builder, obj, node);
            WriteObjectFactoryEnd(builder);
            return true;
        }

        bool GenerateCompositionPathFactory(CodeBuilder builder, CompositionPath obj, ObjectData node)
        {
            var canvasGeometry = NodeFor((CanvasGeometry)obj.Source);
            WriteObjectFactoryStart(builder, node);
            WriteCreateAssignment(builder, node, $"{New} CompositionPath({_stringifier.FactoryCall(canvasGeometry.FactoryCall())})");
            WriteObjectFactoryEnd(builder);
            return true;
        }

        void WriteCreateAssignment(CodeBuilder builder, ObjectData node, string createCallText)
        {
            if (node.RequiresStorage)
            {
                builder.WriteLine($"{Var} result = {node.FieldName} = {createCallText};");
            }
            else
            {
                builder.WriteLine($"{Var} result = {createCallText};");
            }
        }

        // Handles object factories that are just a create call.
        void WriteSimpleObjectFactory(CodeBuilder builder, ObjectData node, string createCallText)
        {
            builder.WriteComment(node.LongComment);
            WriteObjectFactoryStartWithoutCache(builder, node.TypeName, node.Name);
            if (node.RequiresStorage)
            {
                builder.WriteLine($"return {node.FieldName} = {createCallText};");
            }
            else
            {
                builder.WriteLine($"return {createCallText};");
            }
            builder.CloseScope();
            builder.WriteLine();
        }

        void WriteObjectFactoryStart(CodeBuilder builder, ObjectData node, IEnumerable<string> parameters = null)
        {
            builder.WriteComment(node.LongComment);
            WriteObjectFactoryStartWithoutCache(builder, node.TypeName, node.Name, parameters);
        }

        void WriteObjectFactoryStartWithoutCache(CodeBuilder builder, string typeName, string methodName, IEnumerable<string> parameters = null)
        {
            builder.WriteLine($"{_stringifier.ReferenceTypeName(typeName)} {methodName}({(parameters == null ? "" : string.Join(", ", parameters))})");
            builder.OpenScope();
        }

        void WriteObjectFactoryEnd(CodeBuilder builder)
        {
            builder.WriteLine("return result;");
            builder.CloseScope();
            builder.WriteLine();
        }

        // Returns the value from the given keyframe, or null.
        static T? ValueFromKeyFrame<T>(KeyFrameAnimation<T>.KeyFrame kf) where T : struct
        {
            return (kf is KeyFrameAnimation<T>.ValueKeyFrame valueKf) ? (T?)valueKf.Value : null;
        }

        static (T? First, T? Last) FirstAndLastValuesFromKeyFrame<T>(KeyFrameAnimation<T> animation) where T : struct
        {
            return (ValueFromKeyFrame(animation.KeyFrames.First()), ValueFromKeyFrame(animation.KeyFrames.Last()));
        }

        // Returns a string for use in an identifier that describes a ColorKeyFrameAnimation, or null
        // if the animation cannot be described.
        static string DescribeAnimationRange(ColorKeyFrameAnimation animation)
        {
            (var firstValue, var lastValue) = FirstAndLastValuesFromKeyFrame(animation);
            return (firstValue.HasValue && lastValue.HasValue) ? $"{firstValue.Value.Name}_to_{lastValue.Value.Name}" : null;
        }

        string DescribeAnimationRange(ScalarKeyFrameAnimation animation)
        {
            (var firstValue, var lastValue) = FirstAndLastValuesFromKeyFrame(animation);
            return (firstValue.HasValue && lastValue.HasValue) ? $"{FloatId(firstValue.Value)}_to_{FloatId(lastValue.Value)}" : null;
        }

        string DescribeCompositionObject(CompositionObject obj)
        {
            string result = null;
            switch (obj.Type)
            {
                case CompositionObjectType.ColorKeyFrameAnimation:
                    {
                        result = "ColorAnimation";
                        var description = DescribeAnimationRange((ColorKeyFrameAnimation)obj);
                        if (description != null)
                        {
                            result += $"_{description}";
                        }
                    }
                    break;
                case CompositionObjectType.ScalarKeyFrameAnimation:
                    {
                        result = "ScalarAnimation";
                        var description = DescribeAnimationRange((ScalarKeyFrameAnimation)obj);
                        if (description != null)
                        {
                            result += $"_{description}";
                        }
                    }
                    break;
                case CompositionObjectType.Vector2KeyFrameAnimation:
                    result = "Vector2Animation";
                    break;
                case CompositionObjectType.CompositionColorBrush:
                    // Color brushes that are not animated get names describing their color.
                    // Canonicalization ensures there will only be one brush for any one non-animated color.
                    var brush = (CompositionColorBrush)obj;
                    if (brush.Animators.Any())
                    {
                        // Brush is animated. Give it a name based on the colors in the animation.
                        var colorAnimation = (ColorKeyFrameAnimation)(brush.Animators.Where(a => a.Animation is ColorKeyFrameAnimation).First().Animation);
                        var description = DescribeAnimationRange(colorAnimation);
                        if (description != null)
                        {
                            result = $"AnimatedColorBrush_{description}";
                        }
                        else
                        {
                            result = "AnimatedColorBrush";
                        }
                    }
                    else
                    {
                        // Brush is not animated. Give it a name based on the color.
                        result = $"ColorBrush_{brush.Color.Name}";
                    }
                    break;
                case CompositionObjectType.CompositionRectangleGeometry:
                    var rectangle = (CompositionRectangleGeometry)obj;
                    result = $"Rectangle_{Vector2Id(rectangle.Size)}";
                    break;
                case CompositionObjectType.CompositionRoundedRectangleGeometry:
                    var roundedRectangle = (CompositionRoundedRectangleGeometry)obj;
                    result = $"RoundedRectangle_{Vector2Id(roundedRectangle.Size)}";
                    break;
                case CompositionObjectType.CompositionEllipseGeometry:
                    var ellipse = (CompositionEllipseGeometry)obj;
                    result = $"Ellipse_{Vector2Id(ellipse.Radius)}";
                    break;
                case CompositionObjectType.ExpressionAnimation:
                    var expressionAnimation = (ExpressionAnimation)obj;
                    var expression = expressionAnimation.Expression;
                    var expressionType = expression.InferredType;
                    if (expressionType.IsValid && !expressionType.IsGeneric)
                    {
                        result = $"{expressionType.Constraints.ToString()}ExpressionAnimation";
                    }
                    else
                    {
                        result = "ExpressionAnimation";
                    }
                    break;
                default:
                    result = obj.Type.ToString();
                    break;
            }
            // Remove the "Composition" prefix so the name is easier to read.
            const string compositionPrefix = "Composition";
            if (result.StartsWith(compositionPrefix))
            {
                result = result.Substring(compositionPrefix.Length);
            }
            return result;
        }

        void SetCanonicalMethodNames(IEnumerable<ObjectData> canonicals)
        {
            var nodesByTypeName = new Dictionary<string, List<ObjectData>>();
            foreach (var node in canonicals)
            {
                string baseName;

                switch (node.Type)
                {
                    case Graph.NodeType.CompositionObject:
                        baseName = DescribeCompositionObject((CompositionObject)node.Object);
                        break;
                    case Graph.NodeType.CompositionPath:
                        baseName = "CompositionPath";
                        break;
                    case Graph.NodeType.CanvasGeometry:
                        baseName = "Geometry";
                        break;
                    default:
                        throw new InvalidOperationException();
                }

                if (!nodesByTypeName.TryGetValue(baseName, out var nodeList))
                {
                    nodeList = new List<ObjectData>();
                    nodesByTypeName.Add(baseName, nodeList);
                }
                nodeList.Add(node);
            }

            // Set the names on each node.
            foreach (var entry in nodesByTypeName)
            {
                var baseName = entry.Key;
                var nodes = entry.Value;
                if (nodes.Count == 1)
                {
                    // There's only 1 of this type of node. No suffix needed.
                    nodes[0].Name = baseName;
                }
                else
                {
                    // Multiple nodes of this type. Append a counter suffix.
                    for (var i = 0; i < nodes.Count; i++)
                    {
                        nodes[i].Name = $"{baseName}_{i.ToString("000")}";
                    }
                }
            }
        }

        string Const(string value) => $"const {value}";

        string Deref => _stringifier.Deref;

        string New => _stringifier.New;

        string Null => _stringifier.Null;

        string ScopeResolve => _stringifier.ScopeResolve;

        string Var => _stringifier.Var;

        string Bool(bool value) => _stringifier.Bool(value);

        string Color(Color value) => _stringifier.Color(value);

        string IListAdd => _stringifier.IListAdd;

        string CanvasFigureLoop(CanvasFigureLoop value) => _stringifier.CanvasFigureLoop(value);

        string CanvasGeometryCombine(CanvasGeometryCombine value) => _stringifier.CanvasGeometryCombine(value);

        string FilledRegionDetermination(CanvasFilledRegionDetermination value) => _stringifier.FilledRegionDetermination(value);

        string Float(float value) => _stringifier.Float(value);

        // A float for use in an id.
        static string FloatId(float value) => value.ToString("0.###").Replace('.', 'p').Replace('-', 'm');

        string Int(int value) => _stringifier.Int32(value);
        string Int64(long value) => _stringifier.Int64(value);

        string Matrix3x2(Matrix3x2 value) => _stringifier.Matrix3x2(value);

        string Readonly(string value)
        {
            var readonlyPrefix = _stringifier.Readonly;
            return string.IsNullOrWhiteSpace(readonlyPrefix)
                ? value
                : $"{readonlyPrefix} {value}";
        }

        string String(string value) => _stringifier.String(value);

        string StrokeCap(CompositionStrokeCap value)
        {
            switch (value)
            {
                case CompositionStrokeCap.Flat:
                    return $"CompositionStrokeCap{ScopeResolve}Flat";
                case CompositionStrokeCap.Square:
                    return $"CompositionStrokeCap{ScopeResolve}Square";
                case CompositionStrokeCap.Round:
                    return $"CompositionStrokeCap{ScopeResolve}Round";
                case CompositionStrokeCap.Triangle:
                    return $"CompositionStrokeCap{ScopeResolve}Triangle";
                default:
                    throw new InvalidOperationException();
            }
        }

        string StrokeLineJoin(CompositionStrokeLineJoin value)
        {
            switch (value)
            {
                case CompositionStrokeLineJoin.Miter:
                    return $"CompositionStrokeLineJoin{ScopeResolve}Miter";
                case CompositionStrokeLineJoin.Bevel:
                    return $"CompositionStrokeLineJoin{ScopeResolve}Bevel";
                case CompositionStrokeLineJoin.Round:
                    return $"CompositionStrokeLineJoin{ScopeResolve}Round";
                case CompositionStrokeLineJoin.MiterOrBevel:
                    return $"CompositionStrokeLineJoin{ScopeResolve}MiterOrBevel";
                default:
                    throw new InvalidOperationException();
            }
        }

        string TimeSpan(TimeSpan value) => value == _compositionDuration ? _stringifier.TimeSpan(c_durationTicksFieldName) : _stringifier.TimeSpan(value);


        string Vector2(Vector2 value) => _stringifier.Vector2(value);

        // A Vector2 for use in an id.
        static string Vector2Id(Vector2 size)
        {
            return size.X == size.Y
                ? FloatId(size.X)
                : $"{FloatId(size.X)}x{FloatId(size.Y)}";
        }

        string Vector3(Vector3 value) => _stringifier.Vector3(value);

        // Provides language-specific string representations of a value.
        protected internal interface IStringifier
        {
            string Bool(bool value);
            string CanvasFigureLoop(CanvasFigureLoop value);
            string CanvasGeometryCombine(CanvasGeometryCombine value);
            string Color(Color value);
            string Deref { get; }
            string FilledRegionDetermination(CanvasFilledRegionDetermination value);
            string Float(float value);
            string IListAdd { get; }
            string FactoryCall(string value);
            string Int32(int value);
            string Int64(long value);
            string Int64TypeName { get; }
            string Matrix3x2(Matrix3x2 value);
            string MemberSelect { get; }
            string New { get; }
            string Null { get; }
            string Readonly { get; }
            string ReferenceTypeName(string value);
            string ScopeResolve { get; }
            string String(string value);
            string TimeSpan(TimeSpan value);
            string TimeSpan(string ticks);
            string Var { get; }
            string Vector2(Vector2 value);
            string Vector3(Vector3 value);
        }

        /// <summary>
        /// A stringifier implementation for some common string formats that are shared
        /// by most languages.
        /// </summary>
        protected internal abstract class StringifierBase : IStringifier
        {
            public abstract string Deref { get; }

            public abstract string IListAdd { get; }

            public abstract string Int64TypeName { get; }

            public virtual string MemberSelect => ".";

            public abstract string New { get; }

            public abstract string Null { get; }

            public abstract string Readonly { get; }

            public abstract string ScopeResolve { get; }

            public abstract string Var { get; }

            public virtual string Bool(bool value) => value ? "true" : "false";

            public abstract string CanvasFigureLoop(CanvasFigureLoop value);

            public abstract string CanvasGeometryCombine(CanvasGeometryCombine value);

            public abstract string Color(Color value);

            public abstract string FactoryCall(string value);

            public abstract string FilledRegionDetermination(CanvasFilledRegionDetermination value);

            public virtual string Float(float value) =>
                Math.Floor(value) == value
                    ? value.ToString("0")
                    : value.ToString("G9") + "F";

            public virtual string Int32(int value) => value.ToString();

            public abstract string Int64(long value);

            public abstract string Matrix3x2(Matrix3x2 value);

            public abstract string ReferenceTypeName(string value);

            public virtual string String(string value) => $"\"{value}\"";

            public abstract string TimeSpan(TimeSpan value);

            public abstract string TimeSpan(string ticks);

            public abstract string Vector2(Vector2 value);

            public abstract string Vector3(Vector3 value);

            public string Hex(int value) => $"0x{value.ToString("X2")}";
        }


        // A node in the object graph, annotated with extra stuff to assist in code generation.
        sealed class ObjectData : CanonicalizedNode<ObjectData>
        {
            string _overriddenFactoryCall;

            public string Name { get; set; }

            public string FieldName => RequiresStorage ? CamelCase(Name) : null;

            // Returns text for obtaining the value for this node. If the node has
            // been inlined, this can generate the code into the returned string, otherwise
            // it returns code for calling the factory.
            internal string FactoryCall()
            {
                if (Inlined)
                {
                    return _overriddenFactoryCall;
                }
                else
                {
                    return $"{Name}()";
                }
            }

            IEnumerable<string> GetAncestorShortComments()
            {
                // Get the nodes that reference this node.
                var parents = CanonicalInRefs.ToArray();
                if (parents.Length == 1)
                {
                    // There is exactly one parent.
                    if (string.IsNullOrWhiteSpace(parents[0].ShortComment))
                    {
                        // Parent has no comment.
                        yield break;
                    }

                    foreach (var ancestorShortcomment in parents[0].GetAncestorShortComments())
                    {
                        yield return ancestorShortcomment;
                    }
                    yield return parents[0].ShortComment;
                }
            }

            internal string LongComment
            {
                get
                {
                    // Prepend the ancestor nodes.
                    var sb = new StringBuilder();
                    var ancestorIndent = 0;
                    foreach (var ancestorComment in GetAncestorShortComments())
                    {
                        sb.Append(new string(' ', ancestorIndent));
                        sb.AppendLine(ancestorComment);
                        ancestorIndent += 2;
                    }
                    sb.Append(((IDescribable)Object).LongDescription);
                    return sb.ToString();
                }
            }

            internal string ShortComment => ((IDescribable)Object).ShortDescription;

            // True if the object is referenced from more than one method and
            // therefore must be stored after it is created.
            internal bool RequiresStorage { get; set; }

            // Set to indicate that the node relies on Win2D / D2D.
            internal bool RequiresWin2D => Object is CanvasGeometry;

            // True if the code to create the object will be generated inline.
            internal bool Inlined => _overriddenFactoryCall != null;

            internal void ForceInline(string replacementFactoryCall)
            {
                _overriddenFactoryCall = replacementFactoryCall;
            }

            // The name of the type of the object described by this node.
            // This is the name used as the return type of a factory method.
            internal string TypeName
            {
                get
                {
                    switch (Type)
                    {
                        case Graph.NodeType.CompositionObject:
                            return ((CompositionObject)Object).Type.ToString();
                        case Graph.NodeType.CompositionPath:
                            return "CompositionPath";
                        case Graph.NodeType.CanvasGeometry:
                            return "CanvasGeometry";
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }

            // For debugging purposes only.
            public override string ToString() => Name == null ? $"{TypeName} {IsCanonical}" : $"{Name} {IsCanonical}";

            // Sets the first character to lower case.
            static string CamelCase(string value) => $"_{char.ToLowerInvariant(value[0])}{value.Substring(1)}";
        }
    }
}
