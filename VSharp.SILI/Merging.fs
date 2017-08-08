namespace VSharp

open VSharp.Terms

module internal Merging =

    // TODO: This is a pretty performance-critical function. We should store the result into the union itself.
    let internal guardOf = function
        | Terms.GuardedValues(gs, _) -> disjunction gs
        | _ -> Terms.MakeTrue

    let private boolMerge = function
        | [] -> []
        | [_] as gvs -> gvs
        | [(g1, v1); (g2, v2)] -> [(g1 ||| g2, (g1 &&& v1) ||| (g2 &&& v2))]
        | (g, v)::gvs ->
            let guard = List.fold (|||) g (List.map fst gvs) in
            let value = List.fold (fun acc (g, v) -> acc ||| (g &&& v)) (g &&& v) gvs in
            [(guard, value)]

    let rec private structMerge t = function
        | [] -> []
        | [_] as gvs -> gvs
        | gvs ->
            let gs, vs = List.unzip gvs in
            let extractFields = function
                | Struct(fs, _) -> fs
                | t -> "Expected struct, got " + (toString t) |> internalfail
            let fss = vs |> List.map extractFields in
            let merged = Heap.merge gs fss mergeCells in
            let guard = disjunction gs in
            [(guard, Struct(merged, t))]

    and private simplify gvs =
        let rec loop gvs out =
            match gvs with
            | [] -> out
            | (Terms.True, v)::gvs' -> [List.head gvs]
            | (Terms.False, v)::gvs' -> loop gvs' out
            | (g, Union us)::gvs' when not (List.isEmpty us) ->
                let guarded = us |> List.map (fun (g', v) -> (g &&& g', v)) in
                loop gvs' (List.append (simplify guarded) out)
            | gv::gvs' -> loop gvs' (gv::out)
        loop gvs []

    and internal mergeSame = function
        | [] -> []
        | [_] as xs -> xs
        | [(g1, v1); (g2, v2)] as gvs -> if v1 = v2 then [(g1 ||| g2, v1)] else gvs
        | gvs ->
            let rec loop gvs out =
                match gvs with
                | [] -> out
                | (g, v)::gvs' ->
                    let eq, rest = List.partition (snd >> (=) v) gvs' in
                    let joined = List.fold (|||) g (List.map fst eq)
                    match joined with
                    | True -> [(joined, v)]
                    | False -> loop rest out
                    | _ -> loop rest ((joined, v)::out)
            loop gvs []

    and private typedMerge gvs t =
        match t with
        | Bool -> boolMerge gvs
        // TODO: merge generalizations too
        | StructType _
        | ClassType _ ->
            structMerge t gvs
        // TODO: merge arrays too
        | Numeric _
        | String
        | _ -> gvs

    and private compress = function
        | [] -> []
        | [_] as gvs -> gvs
        | [(_, v1); (_, v2)] as gvs when TypeOf v1 = TypeOf v2 -> typedMerge (mergeSame gvs) (TypeOf v1)
        | [_; _] as gvs -> gvs
        | gvs ->
            gvs
                |> mergeSame
                |> List.groupBy (snd >> TypeOf)
                |> List.map (fun (t, gvs) -> typedMerge gvs t)
                |> List.concat

    and internal merge gvs =
        match compress (simplify gvs) with
        | [(True, v)] -> v
        | [(g, v)] when Terms.IsBool v -> g &&& v
        | gvs' -> Union gvs'

    and internal mergeCells gcs =
        let foldCell (acc1, acc2, acc3) (g, (v, c, m)) = ((g, v)::acc1, min acc2 c, max acc3 m) in
        let gvs, c, m = gcs |> List.fold foldCell ([], System.UInt32.MaxValue, System.UInt32.MinValue) in
        (merge gvs, c, m)

    let internal merge2Terms g h u v =
        let g = guardOf u &&& g in
        let h = guardOf v &&& h in
        match g, h with
        | _, _ when u = v -> u
        | True, _
        | _, False
        | False, _
        | _, True -> v
        | Error _, _ -> g
        | _, Error _ -> h
        | _ -> merge [(g, u); (h, v)]

    let internal merge2Cells g h ((u, cu, mu) as ucell : MemoryCell<Term>) ((v, cv, mv) as vcell : MemoryCell<Term>) =
        assert(cu = cv)
        let g = guardOf u &&& g in
        let h = guardOf v &&& h in
        match g, h with
        | _, _ when u = v -> (u, cu, min mu mv)
        | True, _
        | _, False -> ucell
        | False, _
        | _, True -> vcell
        | Error _, _ -> (g, cu, mu)
        | _, Error _ -> (h, cv, mv)
        | _ -> mergeCells [(g, ucell); (h, vcell)]

    let internal merge2States condition1 condition2 state1 state2 =
        match condition1, condition2 with
        | True, _ -> state1
        | False, _ -> state2
        | _, True -> state2
        | _, False -> state1
        | _ -> State.merge2 state1 state2 (merge2Cells condition1 condition2)

    let internal mergeStates conditions states =
        State.merge conditions states mergeCells

    let internal guardedMap mapper gvs =
        let gs, vs = List.unzip gvs in
        vs |> List.map mapper |> List.zip gs |> merge

    let internal guardedStateMap mapper gvs state =
        let gs, vs = List.unzip gvs in
        let vs, states = vs |> List.map mapper |> List.unzip in
        vs |> List.zip gs |> merge, mergeStates gs states
