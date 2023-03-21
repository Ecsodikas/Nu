﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace Nu
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Numerics
open BulletSharp
open Prime
open Nu

/// Tracks Bullet physics bodies by their PhysicsIds.
type internal BulletBodyDictionary = OrderedDictionary<PhysicsId, Vector3 option * RigidBody>

/// Tracks Bullet physics ghosts by their PhysicsIds.
type internal BulletGhostDictionary = OrderedDictionary<PhysicsId, GhostObject>

/// Tracks Bullet physics collision objects by their PhysicsIds.
type internal BulletObjectDictionary = OrderedDictionary<PhysicsId, CollisionObject>

/// Tracks Bullet physics constraints by their PhysicsIds.
type internal BulletConstraintDictionary = OrderedDictionary<PhysicsId, TypedConstraint>

/// The BulletPhysics 3d implementation of PhysicsEngine.
type [<ReferenceEquality>] BulletPhysicsEngine =
    private
        { PhysicsContext : DynamicsWorld
          Constraints : BulletConstraintDictionary
          Bodies : BulletBodyDictionary
          Ghosts : BulletGhostDictionary
          Objects : BulletObjectDictionary
          CollisionConfiguration : CollisionConfiguration
          PhysicsDispatcher : Dispatcher
          BroadPhaseInterface : BroadphaseInterface
          ConstraintSolver : ConstraintSolver
          PhysicsMessages : PhysicsMessage UList
          IntegrationMessages : IntegrationMessage ConcurrentQueue
          mutable RebuildingHack : bool }

    static member make () =
        let physicsMessages = UList.makeEmpty Imperative
        let collisionConfiguration = new DefaultCollisionConfiguration ()
        let physicsDispatcher = new CollisionDispatcher (collisionConfiguration)
        let broadPhaseInterface = new DbvtBroadphase ()
        let constraintSolver = new SequentialImpulseConstraintSolver ()
        let world = new DiscreteDynamicsWorld (physicsDispatcher, broadPhaseInterface, constraintSolver, collisionConfiguration)
        let integrationMessages = ConcurrentQueue ()
        { PhysicsContext = world
          Constraints = OrderedDictionary HashIdentity.Structural
          Bodies = OrderedDictionary HashIdentity.Structural
          Ghosts = OrderedDictionary HashIdentity.Structural
          Objects = OrderedDictionary HashIdentity.Structural
          CollisionConfiguration = collisionConfiguration
          PhysicsDispatcher = physicsDispatcher
          BroadPhaseInterface = broadPhaseInterface
          ConstraintSolver = constraintSolver
          PhysicsMessages = physicsMessages
          IntegrationMessages = integrationMessages
          RebuildingHack = false }

    static member cleanUp physicsEngine =
        physicsEngine.PhysicsContext.Dispose ()
        physicsEngine.ConstraintSolver.Dispose ()
        physicsEngine.BroadPhaseInterface.Dispose ()
        physicsEngine.PhysicsDispatcher.Dispose ()
        physicsEngine.CollisionConfiguration.Dispose ()

    static member private configureBodyShapeProperties (_ : BodyProperties) (_ : BodyShapeProperties option) (_ : ConvexInternalShape) =
        () // NOTE: cannot configure bullet shapes on a per-shape basis.

    static member private configureCollisionObjectProperties (bodyProperties : BodyProperties) (object : CollisionObject) =
        if bodyProperties.Awake
        then object.ActivationState <- object.ActivationState &&& ~~~ActivationState.IslandSleeping
        else object.ActivationState <- object.ActivationState ||| ActivationState.IslandSleeping
        if bodyProperties.AwakeAlways
        then object.ActivationState <- object.ActivationState ||| ActivationState.DisableDeactivation
        else object.ActivationState <- object.ActivationState &&& ~~~ActivationState.DisableDeactivation
        if bodyProperties.Enabled
        then object.ActivationState <- object.ActivationState ||| ActivationState.DisableSimulation
        else object.ActivationState <- object.ActivationState &&& ~~~ActivationState.DisableSimulation
        object.Friction <- bodyProperties.Friction
        object.Restitution <- bodyProperties.Restitution
        match bodyProperties.CollisionDetection with
        | Discontinuous ->
            object.CcdMotionThreshold <- 0.0f
            object.CcdSweptSphereRadius <- 0.0f
        | Continuous continuous ->
            object.CcdMotionThreshold <- continuous.ContinuousMotionThreshold
            object.CcdSweptSphereRadius <- continuous.SweptSphereRadius
        match bodyProperties.BodyType with
        | Static ->
            object.CollisionFlags <- object.CollisionFlags ||| CollisionFlags.StaticObject
            object.CollisionFlags <- object.CollisionFlags &&& ~~~CollisionFlags.KinematicObject
        | Dynamic ->
            object.CollisionFlags <- object.CollisionFlags &&& ~~~CollisionFlags.StaticObject
            object.CollisionFlags <- object.CollisionFlags &&& ~~~CollisionFlags.KinematicObject
        | Kinematic ->
            object.CollisionFlags <- object.CollisionFlags ||| CollisionFlags.KinematicObject
            object.CollisionFlags <- object.CollisionFlags &&& ~~~CollisionFlags.StaticObject

    static member private configureBodyProperties (bodyProperties : BodyProperties) (body : RigidBody) gravity =
        BulletPhysicsEngine.configureCollisionObjectProperties bodyProperties body
        body.MotionState.WorldTransform <- Matrix4x4.CreateFromTrs (bodyProperties.Center, bodyProperties.Rotation, v3One)
        body.LinearVelocity <- bodyProperties.LinearVelocity
        body.AngularVelocity <- bodyProperties.AngularVelocity
        body.AngularFactor <- bodyProperties.AngularFactor
        body.SetDamping (bodyProperties.LinearDamping, bodyProperties.AngularDamping)
        body.Gravity <- match bodyProperties.GravityOverrideOpt with Some gravityOverride -> gravityOverride | None -> gravity

    static member private attachBodyBox sourceSimulant (bodyProperties : BodyProperties) (bodyBox : BodyBox) (compoundShape : CompoundShape) (massAccumulator : single ref) =
        let box = new BoxShape (bodyBox.Size * 0.5f)
        BulletPhysicsEngine.configureBodyShapeProperties bodyProperties bodyBox.PropertiesOpt box
        box.UserObject <-
            { Simulant = sourceSimulant
              BodyId = bodyProperties.BodyId
              ShapeId = match bodyBox.PropertiesOpt with Some p -> p.BodyShapeId | None -> 0UL }
        let mass =
            match bodyProperties.Substance with
            | Density density ->
                let volume = bodyBox.Size.X * bodyBox.Size.Y * bodyBox.Size.Z
                volume * density
            | Mass mass -> mass
        massAccumulator := massAccumulator.Value + mass
        compoundShape.AddChildShape (Matrix4x4.CreateFromTrs (bodyProperties.Center, bodyProperties.Rotation, v3Zero), box)

    static member private attachBodySphere sourceSimulant (bodyProperties : BodyProperties) (bodySphere : BodySphere) (compoundShape : CompoundShape) (massAccumulator : single ref) =
        let sphere = new SphereShape (bodySphere.Radius)
        BulletPhysicsEngine.configureBodyShapeProperties bodyProperties bodySphere.PropertiesOpt sphere
        sphere.UserObject <-
            { Simulant = sourceSimulant
              BodyId = bodyProperties.BodyId
              ShapeId = match bodySphere.PropertiesOpt with Some p -> p.BodyShapeId | None -> 0UL }
        let mass =
            match bodyProperties.Substance with
            | Density density ->
                let volume = 4.0f / 3.0f * MathF.PI * pown bodySphere.Radius 3
                volume * density
            | Mass mass -> mass
        massAccumulator := massAccumulator.Value + mass
        compoundShape.AddChildShape (Matrix4x4.CreateFromTrs (bodyProperties.Center, bodyProperties.Rotation, v3Zero), sphere)

    static member private attachBodyCapsule sourceSimulant (bodyProperties : BodyProperties) (bodyCapsule : BodyCapsule) (compoundShape : CompoundShape) (massAccumulator : single ref) =
        let capsule = new CapsuleShape (bodyCapsule.Radius, bodyCapsule.Height)
        BulletPhysicsEngine.configureBodyShapeProperties bodyProperties bodyCapsule.PropertiesOpt capsule
        capsule.UserObject <-
            { Simulant = sourceSimulant
              BodyId = bodyProperties.BodyId
              ShapeId = match bodyCapsule.PropertiesOpt with Some p -> p.BodyShapeId | None -> 0UL }
        let mass =
            match bodyProperties.Substance with
            | Density density ->
                let volume = MathF.PI * pown bodyCapsule.Radius 2 * (4.0f / 3.0f * bodyCapsule.Radius * bodyCapsule.Height)
                volume * density
            | Mass mass -> mass
        massAccumulator := massAccumulator.Value + mass
        compoundShape.AddChildShape (Matrix4x4.CreateFromTrs (bodyProperties.Center, bodyProperties.Rotation, v3Zero), capsule)

    static member private attachBodyBoxRounded sourceSimulant (bodyProperties : BodyProperties) (bodyBoxRounded : BodyBoxRounded) (compoundShape : CompoundShape) (massAccumulator : single ref) =
        Log.debugOnce "Rounded box not yet implemented via BulletPhysicsEngine; creating a normal box instead."
        let bodyBox = { Center = bodyBoxRounded.Center; Size = bodyBoxRounded.Size; PropertiesOpt = bodyBoxRounded.PropertiesOpt }
        BulletPhysicsEngine.attachBodyBox sourceSimulant bodyProperties bodyBox compoundShape massAccumulator

    //static member private attachBodyTriangle a b c bodyPropertiesOpt (bodyProperties : BodyProperties) (compoundShapeIds : uint64 List) (compoundBuilder : CompoundBuilder array) =
    //    let bodyShapeId = match bodyPropertiesOpt with Some bodyProperties2 -> bodyProperties2.BodyShapeId | None -> 0UL
    //    compoundShapeIds.Add bodyShapeId
    //    let capsule = Triangle (a, b, c)
    //    let ab = (b - a).Magnitude // NOTE: using Heron's formula.
    //    let bc = (c - b).Magnitude
    //    let ca = (a - c).Magnitude
    //    let s = (ab + bc + ca) * 0.5f
    //    let volume = sqrt (s * (s - ab) * (s - bc) * (s - ca))
    //    let mass = volume * bodyProperties.Density
    //    let pose = RigidPose (bodyProperties.Center, bodyProperties.Rotation)
    //    compoundBuilder.[0].Add (&capsule, &pose, mass) // NOTE: passing mass as weight.

    //static member private attachBodyPolygon bodyPolygon bodyProperties compoundShapeIds compoundBuilder =
    //    if bodyPolygon.Vertices.Length >= 3 then
    //        let triangles = Array.windowed 3 bodyPolygon.Vertices
    //        for triangle in triangles do
    //            let (a, b, c) = (triangle.[0], triangle.[1], triangle.[2])
    //            BulletPhysicsEngine.attachBodyTriangle a b c bodyPolygon.PropertiesOpt bodyProperties compoundShapeIds compoundBuilder
    //    else Log.debug "Degenerate polygon sent to BulletPhysicsEngine; 3 or more vertices required."

    static member private attachBodyShapes sourceSimulant bodyProperties bodyShapes compoundShape massAccumulator =
        for bodyShape in bodyShapes do
            BulletPhysicsEngine.attachBodyShape sourceSimulant bodyProperties bodyShape compoundShape massAccumulator

    static member private attachBodyShape sourceSimulant bodyProperties bodyShape compoundShape massAccumulator =
        match bodyShape with
        | BodyEmpty -> ()
        | BodyBox bodyBox -> BulletPhysicsEngine.attachBodyBox sourceSimulant bodyProperties bodyBox compoundShape massAccumulator
        | BodySphere bodySphere -> BulletPhysicsEngine.attachBodySphere sourceSimulant bodyProperties bodySphere compoundShape massAccumulator
        | BodyCapsule bodyCapsule -> BulletPhysicsEngine.attachBodyCapsule sourceSimulant bodyProperties bodyCapsule compoundShape massAccumulator
        | BodyBoxRounded bodyBoxRounded -> BulletPhysicsEngine.attachBodyBoxRounded sourceSimulant bodyProperties bodyBoxRounded compoundShape massAccumulator
        | BodyPolygon bodyPolygon -> () //BulletPhysicsEngine.attachBodyPolygon bodyPolygon bodyProperties compoundShapeIds compoundBuilder
        | BodyShapes bodyShapes -> BulletPhysicsEngine.attachBodyShapes sourceSimulant bodyProperties bodyShapes compoundShape massAccumulator

    static member private createBody3 attachBodyShape sourceId (bodyProperties : BodyProperties) physicsEngine =
        let massAccumulator = ref 0.0f
        let compoundShape = new CompoundShape ()
        attachBodyShape bodyProperties compoundShape massAccumulator
        if bodyProperties.Sensor then
            let motionState = new DefaultMotionState (Matrix4x4.CreateFromTrs (bodyProperties.Center, bodyProperties.Rotation, v3One))
            use constructionInfo = new RigidBodyConstructionInfo (massAccumulator.Value, motionState, compoundShape)
            let body = new RigidBody (constructionInfo)
            BulletPhysicsEngine.configureBodyProperties bodyProperties body physicsEngine.PhysicsContext.Gravity
            physicsEngine.PhysicsContext.AddRigidBody (body, bodyProperties.CollisionCategories, bodyProperties.CollisionMask)
            let physicsId = { SourceId = sourceId; CorrelationId = bodyProperties.BodyId }
            if physicsEngine.Bodies.TryAdd (physicsId, (bodyProperties.GravityOverrideOpt, body))
            then physicsEngine.Objects.Add (physicsId, body)
            else Log.debug ("Could not add body via '" + scstring bodyProperties + "'.")
        else
            let ghost = new GhostObject ()
            ghost.CollisionFlags <- ghost.CollisionFlags &&& ~~~CollisionFlags.NoContactResponse
            BulletPhysicsEngine.configureCollisionObjectProperties bodyProperties ghost
            physicsEngine.PhysicsContext.AddCollisionObject (ghost, bodyProperties.CollisionCategories, bodyProperties.CollisionMask)
            let physicsId = { SourceId = sourceId; CorrelationId = bodyProperties.BodyId }
            if physicsEngine.Ghosts.TryAdd (physicsId, ghost)
            then physicsEngine.Objects.Add (physicsId, ghost)
            else Log.debug ("Could not add body via '" + scstring bodyProperties + "'.")

    static member private createBody4 bodyShape bodyProperties (bodySource : BodySourceInternal) physicsEngine =
        BulletPhysicsEngine.createBody3 (fun ps cs ma ->
            BulletPhysicsEngine.attachBodyShape bodySource.Simulant ps bodyShape cs ma)
            bodySource.BodyId bodyProperties physicsEngine

    static member private createBody (createBodyMessage : CreateBodyMessage) physicsEngine =
        let sourceSimulant = createBodyMessage.SourceSimulant
        let bodyProperties = createBodyMessage.BodyProperties
        let bodySource = { Simulant = sourceSimulant; BodyId = bodyProperties.BodyId }
        BulletPhysicsEngine.createBody4 bodyProperties.BodyShape bodyProperties bodySource physicsEngine

    static member private createBodies (createBodiesMessage : CreateBodiesMessage) physicsEngine =
        List.iter
            (fun bodyProperties ->
                let createBodyMessage =
                    { SourceSimulant = createBodiesMessage.SourceSimulant
                      SourceId = createBodiesMessage.SourceId
                      BodyProperties = bodyProperties }
                BulletPhysicsEngine.createBody createBodyMessage physicsEngine)
            createBodiesMessage.BodiesProperties

    static member private destroyBody (destroyBodyMessage : DestroyBodyMessage) physicsEngine =
        let physicsId = destroyBodyMessage.PhysicsId
        match physicsEngine.Objects.TryGetValue physicsId with
        | (true, object) ->
            match object with
            | :? RigidBody as body ->
                physicsEngine.Objects.Remove physicsId |> ignore
                physicsEngine.Bodies.Remove physicsId |> ignore
                physicsEngine.PhysicsContext.RemoveRigidBody body
            | :? GhostObject as ghost ->
                physicsEngine.Objects.Remove physicsId |> ignore
                physicsEngine.Ghosts.Remove physicsId |> ignore
                physicsEngine.PhysicsContext.RemoveCollisionObject ghost
            | _ -> ()
        | (false, _) ->
            if not physicsEngine.RebuildingHack then
                Log.debug ("Could not destroy non-existent body with PhysicsId = " + scstring physicsId + "'.")

    static member private destroyBodies (destroyBodiesMessage : DestroyBodiesMessage) physicsEngine =
        List.iter (fun physicsId ->
            let destroyBodyMessage : DestroyBodyMessage = { SourceSimulant = destroyBodiesMessage.SourceSimulant; PhysicsId = physicsId }
            BulletPhysicsEngine.destroyBody destroyBodyMessage physicsEngine)
            destroyBodiesMessage.PhysicsIds

    static member private createJoint (createJointMessage : CreateJointMessage) physicsEngine =
        let jointProperties = createJointMessage.JointProperties
        match jointProperties.JointDevice with
        | JointEmpty ->
            ()
        | JointAngle jointAngle ->
            match (physicsEngine.Bodies.TryGetValue jointAngle.TargetId, physicsEngine.Bodies.TryGetValue jointAngle.TargetId2) with
            | ((true, (_, body)), (true, (_, body2))) ->
                let hinge = new HingeConstraint (body, body2, jointAngle.Anchor, jointAngle.Anchor2, jointAngle.Axis, jointAngle.Axis2)
                hinge.SetLimit (jointAngle.AngleMin, jointAngle.AngleMax, jointAngle.Softness, jointAngle.BiasFactor, jointAngle.RelaxationFactor)
                hinge.BreakingImpulseThreshold <- jointAngle.BreakImpulseThreshold
                physicsEngine.PhysicsContext.AddConstraint hinge
                let physicsId = { SourceId = createJointMessage.SourceId; CorrelationId = jointProperties.JointId }
                if physicsEngine.Constraints.TryAdd (physicsId, hinge)
                then () // nothing to do
                else Log.debug ("Could not add joint via '" + scstring jointProperties + "'.")
            | (_, _) -> Log.debug "Could not set create a joint for one or more non-existent bodies."
        | _ -> failwithnie ()

    static member private createJoints (createJointsMessage : CreateJointsMessage) physicsEngine =
        List.iter
            (fun jointProperties ->
                let createJointMessage =
                    { SourceSimulant = createJointsMessage.SourceSimulant
                      SourceId = createJointsMessage.SourceId
                      JointProperties = jointProperties }
                BulletPhysicsEngine.createJoint createJointMessage physicsEngine)
            createJointsMessage.JointsProperties

    static member private destroyJoint (destroyJointMessage : DestroyJointMessage) physicsEngine =
        match physicsEngine.Constraints.TryGetValue destroyJointMessage.PhysicsId with
        | (true, contrain) ->
            physicsEngine.Constraints.Remove destroyJointMessage.PhysicsId |> ignore
            physicsEngine.PhysicsContext.RemoveConstraint contrain
        | (false, _) ->
            if not physicsEngine.RebuildingHack then
                Log.debug ("Could not destroy non-existent joint with PhysicsId = " + scstring destroyJointMessage.PhysicsId + "'.")

    static member private destroyJoints (destroyJointsMessage : DestroyJointsMessage) physicsEngine =
        List.iter (fun physicsId ->
            let destroyJointMessage = { SourceSimulant = destroyJointsMessage.SourceSimulant; PhysicsId = physicsId }
            BulletPhysicsEngine.destroyJoint destroyJointMessage physicsEngine)
            destroyJointsMessage.PhysicsIds

    static member private setBodyEnabled (setBodyEnabledMessage : SetBodyEnabledMessage) physicsEngine =
        match physicsEngine.Objects.TryGetValue setBodyEnabledMessage.PhysicsId with
        | (true, object) ->
            if setBodyEnabledMessage.Enabled
            then object.ActivationState <- object.ActivationState ||| ActivationState.DisableSimulation
            else object.ActivationState <- object.ActivationState &&& ~~~ActivationState.DisableSimulation
        | (false, _) -> Log.debug ("Could not set enabled of non-existent body with PhysicsId = " + scstring setBodyEnabledMessage.PhysicsId + "'.")

    static member private setBodyCenter (setBodyCenterMessage : SetBodyCenterMessage) physicsEngine =
        match physicsEngine.Objects.TryGetValue setBodyCenterMessage.PhysicsId with
        | (true, object) ->
            let mutable transform = object.WorldTransform
            transform.Translation <- setBodyCenterMessage.Center
            object.WorldTransform <- transform
        | (false, _) -> Log.debug ("Could not set center of non-existent body with PhysicsId = " + scstring setBodyCenterMessage.PhysicsId + "'.")

    static member private setBodyRotation (setBodyRotationMessage : SetBodyRotationMessage) physicsEngine =
        match physicsEngine.Objects.TryGetValue setBodyRotationMessage.PhysicsId with
        | (true, object) -> object.WorldTransform <- object.WorldTransform.SetRotation setBodyRotationMessage.Rotation
        | (false, _) -> Log.debug ("Could not set rotation of non-existent body with PhysicsId = " + scstring setBodyRotationMessage.PhysicsId + "'.")

    static member private setBodyAngularVelocity (setBodyAngularVelocityMessage : SetBodyAngularVelocityMessage) physicsEngine =
        match physicsEngine.Objects.TryGetValue setBodyAngularVelocityMessage.PhysicsId with
        | (true, (:? RigidBody as body)) -> body.AngularVelocity <- setBodyAngularVelocityMessage.AngularVelocity
        | (true, _) -> () // nothing to do
        | (false, _) -> Log.debug ("Could not set angular velocity of non-existent body with PhysicsId = " + scstring setBodyAngularVelocityMessage.PhysicsId + "'.")

    static member private applyBodyAngularImpulse (applyBodyAngularImpulseMessage : ApplyBodyAngularImpulseMessage) physicsEngine =
        match physicsEngine.Objects.TryGetValue applyBodyAngularImpulseMessage.PhysicsId with
        | (true, (:? RigidBody as body)) -> body.ApplyTorqueImpulse (applyBodyAngularImpulseMessage.AngularImpulse)
        | (true, _) -> () // nothing to do
        | (false, _) -> Log.debug ("Could not apply angular impulse to non-existent body with PhysicsId = " + scstring applyBodyAngularImpulseMessage.PhysicsId + "'.")

    static member private setBodyLinearVelocity (setBodyLinearVelocityMessage : SetBodyLinearVelocityMessage) physicsEngine =
        match physicsEngine.Objects.TryGetValue setBodyLinearVelocityMessage.PhysicsId with
        | (true, (:? RigidBody as body)) -> body.LinearVelocity <- setBodyLinearVelocityMessage.LinearVelocity
        | (true, _) -> () // nothing to do
        | (false, _) -> Log.debug ("Could not set linear velocity of non-existent body with PhysicsId = " + scstring setBodyLinearVelocityMessage.PhysicsId + "'.")

    static member private applyBodyLinearImpulse (applyBodyLinearImpulseMessage : ApplyBodyLinearImpulseMessage) physicsEngine =
        match physicsEngine.Objects.TryGetValue applyBodyLinearImpulseMessage.PhysicsId with
        | (true, (:? RigidBody as body)) -> body.ApplyImpulse (applyBodyLinearImpulseMessage.LinearImpulse, applyBodyLinearImpulseMessage.Offset)
        | (true, _) -> () // nothing to do
        | (false, _) -> Log.debug ("Could not apply linear impulse to non-existent body with PhysicsId = " + scstring applyBodyLinearImpulseMessage.PhysicsId + "'.")

    static member private applyBodyForce (applyBodyForceMessage : ApplyBodyForceMessage) physicsEngine =
        match physicsEngine.Objects.TryGetValue applyBodyForceMessage.PhysicsId with
        | (true, (:? RigidBody as body)) -> body.ApplyForce (applyBodyForceMessage.Force, applyBodyForceMessage.Offset)
        | (true, _) -> () // nothing to do
        | (false, _) -> Log.debug ("Could not apply force to non-existent body with PhysicsId = " + scstring applyBodyForceMessage.PhysicsId + "'.")

    static member private applyBodyTorque (applyBodyTorqueMessage : ApplyBodyTorqueMessage) physicsEngine =
        match physicsEngine.Objects.TryGetValue applyBodyTorqueMessage.PhysicsId with
        | (true, (:? RigidBody as body)) -> body.ApplyTorque applyBodyTorqueMessage.Torque
        | (true, _) -> () // nothing to do
        | (false, _) -> Log.debug ("Could not apply torque to non-existent body with PhysicsId = " + scstring applyBodyTorqueMessage.PhysicsId + "'.")

    static member private handlePhysicsMessage physicsEngine physicsMessage =
        match physicsMessage with
        | CreateBodyMessage createBodyMessage -> BulletPhysicsEngine.createBody createBodyMessage physicsEngine
        | CreateBodiesMessage createBodiesMessage -> BulletPhysicsEngine.createBodies createBodiesMessage physicsEngine
        | DestroyBodyMessage destroyBodyMessage -> BulletPhysicsEngine.destroyBody destroyBodyMessage physicsEngine
        | DestroyBodiesMessage destroyBodiesMessage -> BulletPhysicsEngine.destroyBodies destroyBodiesMessage physicsEngine
        | CreateJointMessage createJointMessage -> BulletPhysicsEngine.createJoint createJointMessage physicsEngine
        | CreateJointsMessage createJointsMessage -> BulletPhysicsEngine.createJoints createJointsMessage physicsEngine
        | DestroyJointMessage destroyJointMessage -> BulletPhysicsEngine.destroyJoint destroyJointMessage physicsEngine
        | DestroyJointsMessage destroyJointsMessage -> BulletPhysicsEngine.destroyJoints destroyJointsMessage physicsEngine
        | SetBodyEnabledMessage setBodyEnabledMessage -> BulletPhysicsEngine.setBodyEnabled setBodyEnabledMessage physicsEngine
        | SetBodyCenterMessage setBodyCenterMessage -> BulletPhysicsEngine.setBodyCenter setBodyCenterMessage physicsEngine
        | SetBodyRotationMessage setBodyRotationMessage -> BulletPhysicsEngine.setBodyRotation setBodyRotationMessage physicsEngine
        | SetBodyAngularVelocityMessage setBodyAngularVelocityMessage -> BulletPhysicsEngine.setBodyAngularVelocity setBodyAngularVelocityMessage physicsEngine
        | ApplyBodyAngularImpulseMessage applyBodyAngularImpulseMessage -> BulletPhysicsEngine.applyBodyAngularImpulse applyBodyAngularImpulseMessage physicsEngine
        | SetBodyLinearVelocityMessage setBodyLinearVelocityMessage -> BulletPhysicsEngine.setBodyLinearVelocity setBodyLinearVelocityMessage physicsEngine
        | ApplyBodyLinearImpulseMessage applyBodyLinearImpulseMessage -> BulletPhysicsEngine.applyBodyLinearImpulse applyBodyLinearImpulseMessage physicsEngine
        | ApplyBodyForceMessage applyBodyForceMessage -> BulletPhysicsEngine.applyBodyForce applyBodyForceMessage physicsEngine
        | ApplyBodyTorqueMessage applyBodyTorqueMessage -> BulletPhysicsEngine.applyBodyTorque applyBodyTorqueMessage physicsEngine
        | SetGravityMessage gravity ->
            physicsEngine.PhysicsContext.Gravity <- gravity
            for (gravityOverrideOpt, body) in physicsEngine.Bodies.Values do
                match gravityOverrideOpt with
                | Some gravityOverride -> body.Gravity <- gravityOverride
                | None -> body.Gravity <- gravity
        | RebuildPhysicsHackMessage ->
            physicsEngine.RebuildingHack <- true
            for constrain in physicsEngine.Constraints.Values do physicsEngine.PhysicsContext.RemoveConstraint constrain
            physicsEngine.Objects.Clear ()
            physicsEngine.Constraints.Clear ()
            for ghost in physicsEngine.Ghosts.Values do physicsEngine.PhysicsContext.RemoveCollisionObject ghost
            physicsEngine.Ghosts.Clear ()
            for (_, body) in physicsEngine.Bodies.Values do physicsEngine.PhysicsContext.RemoveRigidBody body
            physicsEngine.Bodies.Clear ()
            physicsEngine.IntegrationMessages.Clear ()

    static member private integrate stepTime physicsEngine =
        let physicsStepAmount =
            match (Constants.GameTime.DesiredFrameRate, stepTime) with
            | (StaticFrameRate frameRate, UpdateTime frames) -> 1.0f / single frameRate * single frames
            | (DynamicFrameRate _, ClockTime secs) -> secs
            | (_, _) -> failwithumf ()
        if physicsStepAmount > 0.0f then
            let result = physicsEngine.PhysicsContext.StepSimulation physicsStepAmount
            ignore result

    static member private createIntegrationMessages physicsEngine =
        for (_, body) in physicsEngine.Bodies.Values do
            let asleep = int body.ActivationState &&& int ActivationState.IslandSleeping <> 0
            if not asleep then
                let bodyTransformMessage =
                    BodyTransformMessage
                        { BodySource = body.UserObject :?> BodySourceInternal
                          Center = body.MotionState.WorldTransform.Translation
                          Rotation = body.MotionState.WorldTransform.Rotation
                          LinearVelocity = body.LinearVelocity
                          AngularVelocity = body.AngularVelocity }
                physicsEngine.IntegrationMessages.Enqueue bodyTransformMessage

    static member private handlePhysicsMessages physicsMessages physicsEngine =
        for physicsMessage in physicsMessages do
            BulletPhysicsEngine.handlePhysicsMessage physicsEngine physicsMessage
        physicsEngine.RebuildingHack <- false

    interface PhysicsEngine with

        member physicsEngine.BodyExists physicsId =
            physicsEngine.Objects.ContainsKey physicsId

        member physicsEngine.GetBodyContactNormals physicsId =
            // TODO: see if this can be optimized from linear search to constant-time look-up.
            match physicsEngine.Objects.TryGetValue physicsId with
            | (true, object) ->
                let dispatcher = physicsEngine.PhysicsContext.Dispatcher
                let manifoldCount = dispatcher.NumManifolds
                [for i in 0 .. dec manifoldCount do
                    let manifold = dispatcher.GetManifoldByIndexInternal i
                    if manifold.Body0 = object then
                        let contactCount = manifold.NumContacts
                        for j in 0 .. dec contactCount do
                            let contact = manifold.GetContactPoint j
                            yield contact.NormalWorldOnB]
            | (false, _) -> []

        member physicsEngine.GetBodyLinearVelocity physicsId =
            match physicsEngine.Bodies.TryGetValue physicsId with
            | (true, (_, body)) -> body.LinearVelocity
            | (false, _) ->
                if physicsEngine.Ghosts.ContainsKey physicsId then v3Zero
                else failwith ("No body with PhysicsId = " + scstring physicsId + ".")

        member physicsEngine.GetBodyToGroundContactNormals physicsId =
            List.filter
                (fun normal ->
                    let theta = Vector3.Dot (normal, Vector3.UnitY) |> double |> Math.Acos |> Math.Abs
                    theta < Math.PI * 0.25)
                ((physicsEngine :> PhysicsEngine).GetBodyContactNormals physicsId)

        member physicsEngine.GetBodyToGroundContactNormalOpt physicsId =
            let groundNormals = (physicsEngine :> PhysicsEngine).GetBodyToGroundContactNormals physicsId
            match groundNormals with
            | [] -> None
            | _ ->
                let averageNormal = List.reduce (fun normal normal2 -> (normal + normal2) * 0.5f) groundNormals
                Some averageNormal

        member physicsEngine.GetBodyToGroundContactTangentOpt physicsId =
            match (physicsEngine :> PhysicsEngine).GetBodyToGroundContactNormalOpt physicsId with
            | Some normal -> Some (Vector3.Cross (v3Forward, normal))
            | None -> None

        member physicsEngine.IsBodyOnGround physicsId =
            let groundNormals = (physicsEngine :> PhysicsEngine).GetBodyToGroundContactNormals physicsId
            List.notEmpty groundNormals

        member physicsEngine.PopMessages () =
            let messages = physicsEngine.PhysicsMessages
            let physicsEngine = { physicsEngine with PhysicsMessages = UList.makeEmpty (UList.getConfig physicsEngine.PhysicsMessages) }
            (messages, physicsEngine :> PhysicsEngine)

        member physicsEngine.ClearMessages () =
            let physicsEngine = { physicsEngine with PhysicsMessages = UList.makeEmpty (UList.getConfig physicsEngine.PhysicsMessages) }
            physicsEngine :> PhysicsEngine

        member physicsEngine.EnqueueMessage physicsMessage =
#if HANDLE_PHYSICS_MESSAGES_IMMEDIATE
            BulletPhysicsEngine.handlePhysicsMessage physicsEngine physicsMessage
            physicsEngine
#else
            let physicsMessages = UList.add physicsMessage physicsEngine.PhysicsMessages
            let physicsEngine = { physicsEngine with PhysicsMessages = physicsMessages }
            physicsEngine :> PhysicsEngine
#endif

        member physicsEngine.Integrate stepTime physicsMessages =
            BulletPhysicsEngine.handlePhysicsMessages physicsMessages physicsEngine
            BulletPhysicsEngine.integrate stepTime physicsEngine
            BulletPhysicsEngine.createIntegrationMessages physicsEngine
            let integrationMessages = SegmentedArray.ofSeq physicsEngine.IntegrationMessages
            physicsEngine.IntegrationMessages.Clear ()
            integrationMessages