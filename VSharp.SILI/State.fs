﻿namespace VSharp
open VSharp.Utils

module internal State =
    module SymbolicHeap = Heap

    let internal pointerType = Types.pointerType

    type internal stack = MappedStack.stack<StackKey, MemoryCell<Term>>
    type internal heap = SymbolicHeap
    type internal staticMemory = SymbolicHeap
    type internal pathCondition = Term list
    type internal stackFrame = (FunctionIdentifier * pathCondition) option * (StackKey * TermMetadata * TermType) list * Timestamp
    type internal frames = Stack.stack<stackFrame> * StackHash
    type internal state = stack * heap * staticMemory * frames * pathCondition

// ------------------------------- Primitives -------------------------------

    let internal empty : state = (MappedStack.empty, SymbolicHeap.empty, SymbolicHeap.empty, (Stack.empty, List.empty), List.empty)

    type internal 'a SymbolicValue =
        | Specified of 'a
        | Unspecified

    let internal nameOfLocation = term >> function
        | HeapRef(((_, t), []), _) -> toString t
        | StackRef((name, _), []) -> name
        | StaticRef(name, []) -> System.Type.GetType(name).FullName
        | HeapRef((_, path), _)
        | StackRef(_, path)
        | StaticRef(_, path) -> path |> Seq.map (fst >> toString) |> join "."
        | l -> "requested name of an unexpected location " + (toString l) |> internalfail

    let internal readStackLocation ((s, _, _, _, _) : state) key = MappedStack.find key s
    let internal readHeapLocation ((_, h, _, _, _) : state) key = h.[key] |> fst3
    let internal readStaticLocation ((_, _, m, _, _) : state) key = m.[key] |> fst3

    let internal isAllocatedOnStack ((s, _, _, _, _) : state) key = MappedStack.containsKey key s
    let internal isAllocatedInHeap ((_, h, _, _, _) : state) key = Heap.contains key h
    let internal staticMembersInitialized ((_, _, m, _, _) : state) typeName =
        SymbolicHeap.contains (Terms.MakeStringKey typeName) m

    let internal newStackFrame time metadata ((s, h, m, (f, sh), p) : state) funcId frame : state =
        let pushOne (map : stack) (key, value, typ) =
            match value with
            | Specified term -> ((key, metadata, typ), MappedStack.push key (term, time, time) map)
            | Unspecified -> ((key, metadata, typ), MappedStack.reserve key map)
        in
        let frameMetadata = Some(funcId, p) in
        let locations, newStack = frame |> List.mapFold pushOne s in
        let f' = Stack.push f (frameMetadata, locations, time) in
        let sh' = frameMetadata.GetHashCode()::sh in
        (newStack, h, m, (f', sh'), p)

    let internal newScope time metadata ((s, h, m, (f, sh), p) : state) frame : state =
        let pushOne (map : stack) (key, value, typ) =
            match value with
            | Specified term -> ((key, metadata, typ), MappedStack.push key (term, time, time) map)
            | Unspecified -> ((key, metadata, typ), MappedStack.reserve key map)
        in
        let locations, newStack = frame |> List.mapFold pushOne s in
        (newStack, h, m, (Stack.push f (None, locations, time), sh), p)

    let internal pushToCurrentStackFrame ((s, _, _, _, _) : state) key value = MappedStack.push key value s
    let internal popStack ((s, h, m, (f, sh), p) : state) : state =
        let popOne (map : stack) (name, _, _) = MappedStack.remove map name
        let metadata, locations, _ = Stack.peak f in
        let f' = Stack.pop f in
        let sh' =
            match metadata with
            | Some _ ->
                assert(not <| List.isEmpty sh)
                List.tail sh
            | None -> sh
        (List.fold popOne s locations, h, m, (f', sh'), p)

    let internal writeStackLocation ((s, h, m, f, p) : state) key value : state =
        (MappedStack.add key value s, h, m, f, p)

    let internal stackFold = MappedStack.fold

    let internal frameTime ((_, _, _, (f, _), _) : state) key =
        match List.tryFind (snd3 >> List.exists (fst3 >> ((=) key))) f with
        | Some(_, _, t) -> t
        | None -> internalfailf "stack does not contain key %O!" key

    let internal typeOfStackLocation ((_, _, _, (f, _), _) : state) key =
        match List.tryPick (snd3 >> List.tryPick (fun (l, _, t) -> if l = key then Some t else None)) f with
        | Some t -> t
        | None -> internalfailf "stack does not contain key %O!" key

    let internal metadataOfStackLocation ((_, _, _, (f, _), _) : state) key =
        match List.tryPick (snd3 >> List.tryPick (fun (l, m, _) -> if l = key then Some m else None)) f with
        | Some t -> t
        | None -> internalfailf "stack does not contain key %O!" key

    let internal compareStacks s1 s2 = MappedStack.compare (fun key value -> StackRef key [] []) fst3 s1 s2

    let internal withPathCondition ((s, h, m, f, p) : state) cond : state = (s, h, m, f, cond::p)
    let internal popPathCondition ((s, h, m, f, p) : state) : state =
        match p with
        | [] -> internalfail "cannot pop empty path condition"
        | _::p' -> (s, h, m, f, p')

    let private stackOf ((s, _, _, _, _) : state) = s
    let internal heapOf ((_, h, _, _, _) : state) = h
    let internal staticsOf ((_, _, m, _, _) : state) = m
    let private framesOf ((_, _, _, f, _) : state) = f
    let private framesHashOf ((_, _, _, (_, h), _) : state) = h
    let internal pathConditionOf ((_, _, _, _, p) : state) = p

    let internal withHeap ((s, h, m, f, p) : state) h' = (s, h', m, f, p)
    let internal withStatics ((s, h, m, f, p) : state) m' = (s, h, m', f, p)

    let internal stackLocationToReference state location =
        StackRef location [] (metadataOfStackLocation state location)
    let internal staticLocationToReference term =
        match term.term with
        | Concrete(location, String) -> StaticRef (location :?> string) [] term.metadata
        | _ -> __notImplemented__()

    let private staticKeyToString = term >> function
        | Concrete(typeName, String) -> System.Type.GetType(typeName :?> string).FullName
        | t -> toString t

    let internal mkMetadata location state =
        [{ location = location; stack = framesHashOf state }]

// ------------------------------- Memory level -------------------------------

    type IActivator =
        abstract member CreateInstance : TermMetadata -> System.Type -> Term list -> state -> (Term * state)
    type private NullActivator() =
        interface IActivator with
            member this.CreateInstance _ _ _ _ =
                internalfail "activator is not ready"
    let mutable activator : IActivator = new NullActivator() :> IActivator
    let mutable genericLazyInstantiator : TermMetadata -> Timestamp -> Term -> TermType -> unit -> Term =
        fun _ _ _ _ () -> internalfailf "generic lazy instantiator is not ready"

    let internal stackLazyInstantiator state time key =
        let time = frameTime state key in
        let t = typeOfStackLocation state key in
        let metadata = metadataOfStackLocation state key in
        let fql = StackRef key [] metadata in
        (genericLazyInstantiator metadata time fql t (), time, time)

    let internal dumpMemory ((_, h, m, _, _) : state) =
        let sh = Heap.dump h toString in
        let mh = Heap.dump m staticKeyToString in
        let separator = if System.String.IsNullOrWhiteSpace(sh) then "" else "\n"
        sh + separator + mh

// ------------------------------- Merging -------------------------------

    let internal merge2 ((s1, h1, m1, f1, p1) as state : state) ((s2, h2, m2, f2, p2) : state) resolve : state =
        assert(p1 = p2)
        assert(f1 = f2)
        let mergedStack = MappedStack.merge2 s1 s2 resolve (stackLazyInstantiator state) in
        let mergedHeap = Heap.merge2 h1 h2 resolve in
        let mergedStatics = Heap.merge2 m1 m2 resolve in
        (mergedStack, mergedHeap, mergedStatics, f1, p1)

    let internal merge guards states resolve : state =
        assert(List.length states > 0)
        let first = List.head states in
        let frames = framesOf first in
        let path = pathConditionOf first in
        assert(List.forall (fun s -> framesOf s = frames) states)
        assert(List.forall (fun s -> pathConditionOf s = path) states)
        let mergedStack = MappedStack.merge guards (List.map stackOf states) resolve (stackLazyInstantiator first) in
        let mergedHeap = Heap.merge guards (List.map heapOf states) resolve in
        let mergedStatics = Heap.merge guards (List.map staticsOf states) resolve in
        (mergedStack, mergedHeap, mergedStatics, frames, path)
