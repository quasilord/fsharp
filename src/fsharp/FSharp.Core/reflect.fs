//----------------------------------------------------------------------------
// Copyright (c) 2002-2012 Microsoft Corporation. 
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.html file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
//----------------------------------------------------------------------------

// Reflection on F# values. Analyze an object to see if it the representation
// of an F# value.

namespace Microsoft.FSharp.Reflection 

open System
open System.Globalization
open System.Reflection
open Microsoft.FSharp.Core
open Microsoft.FSharp.Core.Operators
open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators
open Microsoft.FSharp.Collections
open Microsoft.FSharp.Primitives.Basics

#if NET_CORE
type internal AttributeValue = System.Attribute

type internal BindingFlags =
    | Default = 0
    | IgnoreCase = 1
    | DeclaredOnly = 2
    | Instance = 4
    | Static = 8
    | Public = 16
    | NonPublic = 32
    | FlattenHierarchy = 64
    | InvokeMethod = 256
    | CreateInstance = 512
    | GetField = 1024
    | SetField = 2048
    | GetProperty = 4096
    | SetProperty = 8192
    | PutDispProperty = 16384
    | PutRefDispProperty = 32768
    | ExactBinding = 65536
    | SuppressChangeType = 131072
    | OptionalParamBinding = 262144
    | IgnoreReturn = 16777216
#else
type internal AttributeValue = obj
#endif

[<AutoOpen>]
module internal Utilities =

#if NET_CORE
    type System.Type with 
        member x.IsGenericType = x.GetTypeInfo().IsGenericType
        member x.IsGenericTypeDefinition = x.GetTypeInfo().IsGenericTypeDefinition
        member x.GetGenericArguments() = x.GetTypeInfo().GenericTypeArguments
        member x.GetNestedType(nm:string, _bindingFlags:BindingFlags) = x.GetTypeInfo().GetDeclaredNestedType(nm).AsType()
        member x.GetMethods(_bindingFlags:BindingFlags) = x.GetTypeInfo().DeclaredMethods |> Seq.toArray
        member x.GetMethods() = x.GetTypeInfo().DeclaredMethods |> Seq.toArray
        member x.GetCustomAttributes(attributeType,inherrit) = x.GetTypeInfo().GetCustomAttributes(attributeType,inherrit) |> Seq.toArray
        member x.GetFields(_bindingFlags:BindingFlags:BindingFlags) = x.GetTypeInfo().DeclaredFields |> Seq.toArray
        member x.GetProperty(propName,_bindingFlags:BindingFlags) = x.GetTypeInfo().GetDeclaredProperty(propName) 
        // Note: This is approximate - it works based on the number of arguments
        member x.GetConstructor(_bindingFlags:BindingFlags,_binder,argTypes:Type[],_arg4) = x.GetTypeInfo().DeclaredConstructors |> Seq.find (fun n -> n.GetParameters().Length = argTypes.Length)
        member x.GetMethod(methName,_bindingFlags:BindingFlags) = x.GetTypeInfo().GetDeclaredMethod(methName)
        member x.GetMethod(methName,_bindingFlags:BindingFlags,_binder,_argTypes,_returnType) = x.GetTypeInfo().GetDeclaredMethod(methName)
        member x.GetProperties(_bindingFlags:BindingFlags) = x.GetTypeInfo().DeclaredProperties |> Seq.toArray
        member x.GetProperties() = x.GetTypeInfo().DeclaredProperties |> Seq.toArray
        member x.GetInterfaces() = x.GetTypeInfo().ImplementedInterfaces |> Seq.toArray
        member x.BaseType = x.GetTypeInfo().BaseType

    type System.Reflection.PropertyInfo with 
        member x.GetValue(obj,_bindingFlags,_arg3,_arg4,_arg5) = x.GetValue(obj)

    type System.Reflection.MethodInfo with 
        member x.Invoke(obj,_bindingFlags,_arg3,args,_arg5) = x.Invoke(obj,args)
        member x.GetCustomAttributesData() = x.CustomAttributes

    type System.Reflection.ConstructorInfo with 
        member x.Invoke(_bindingFlags,_arg3,args,_arg5) = x.Invoke(args)
#endif

module internal Impl =

    let debug = false

    let inline checkNonNull argName (v: 'T) = 
        match box v with 
        | null -> nullArg argName 
        | _ -> ()
        
    let emptyArray arr = (Array.length arr = 0)
    let nonEmptyArray arr = Array.length arr > 0

    let isNamedType(typ:Type) = not (typ.IsArray || typ.IsByRef || typ.IsPointer)

    let equivHeadTypes (ty1:Type) (ty2:Type) = 
        isNamedType(ty1) &&
        if ty1.IsGenericType then 
          ty2.IsGenericType && (ty1.GetGenericTypeDefinition()).Equals(ty2.GetGenericTypeDefinition())
        else 
          ty1.Equals(ty2)

    let option = typedefof<obj option>
    let func = typedefof<(obj -> obj)>

    let isOptionType typ = equivHeadTypes typ (typeof<int option>)
    let isFunctionType typ = equivHeadTypes typ (typeof<(int -> int)>)
    let isListType typ = equivHeadTypes typ (typeof<int list>)

    //-----------------------------------------------------------------
    // GENERAL UTILITIES


#if FX_NO_BINDING_FLAGS
    let instancePropertyFlags = BindingFlags.Instance 
    let staticPropertyFlags = BindingFlags.Static
    let staticFieldFlags = BindingFlags.Static 
    let staticMethodFlags = BindingFlags.Static 
#else
 
#if FX_ATLEAST_PORTABLE
    let instancePropertyFlags = BindingFlags.Instance 
    let staticPropertyFlags = BindingFlags.Static
    let staticFieldFlags = BindingFlags.Static 
    let staticMethodFlags = BindingFlags.Static 
#else    
    let instancePropertyFlags = BindingFlags.GetProperty ||| BindingFlags.Instance 
    let staticPropertyFlags = BindingFlags.GetProperty ||| BindingFlags.Static
    let staticFieldFlags = BindingFlags.GetField ||| BindingFlags.Static 
    let staticMethodFlags = BindingFlags.Static 
#endif
#endif

    let getInstancePropertyInfo (typ: Type,propName,bindingFlags) = typ.GetProperty(propName,instancePropertyFlags ||| bindingFlags) 
    let getInstancePropertyInfos (typ,names,bindingFlags) = names |> Array.map (fun nm -> getInstancePropertyInfo (typ,nm,bindingFlags)) 

    let getInstancePropertyReader (typ: Type,propName,bindingFlags) =
        match getInstancePropertyInfo(typ, propName, bindingFlags) with
        | null -> None
#if FX_ATLEAST_PORTABLE
        | prop -> Some(fun (obj:obj) -> prop.GetValue(obj,null))
#else        
        | prop -> Some(fun (obj:obj) -> prop.GetValue(obj,instancePropertyFlags ||| bindingFlags,null,null,null))
#endif
    //-----------------------------------------------------------------
    // ATTRIBUTE DECOMPILATION


    let tryFindCompilationMappingAttribute (attrs:AttributeValue[]) =
      match attrs with
      | null | [| |] -> None
      | [| res |] -> let a = (res :?> CompilationMappingAttribute) in Some (a.SourceConstructFlags, a.SequenceNumber, a.VariantNumber)
      | _ -> raise <| System.InvalidOperationException (SR.GetString(SR.multipleCompilationMappings))

    let findCompilationMappingAttribute (attrs:AttributeValue[]) =
      match tryFindCompilationMappingAttribute attrs with
      | None -> failwith "no compilation mapping attribute"
      | Some a -> a

#if FX_NO_CUSTOMATTRIBUTEDATA
    let tryFindCompilationMappingAttributeFromType       (typ:Type)        = tryFindCompilationMappingAttribute ( typ.GetCustomAttributes (typeof<CompilationMappingAttribute>,false))
    let tryFindCompilationMappingAttributeFromMemberInfo (info:MemberInfo) = tryFindCompilationMappingAttribute (info.GetCustomAttributes (typeof<CompilationMappingAttribute>,false))
    let    findCompilationMappingAttributeFromMemberInfo (info:MemberInfo) =    findCompilationMappingAttribute (info.GetCustomAttributes (typeof<CompilationMappingAttribute>,false))
#else
    let cmaName = typeof<CompilationMappingAttribute>.FullName
    
    let tryFindCompilationMappingAttributeFromData (attrs:System.Collections.Generic.IList<CustomAttributeData>) =
        match attrs with
        | null -> None
        | _ -> 
            let mutable res = None
            for a in attrs do
#if NET_CORE
                if a.AttributeType.FullName = cmaName then 
#else
                if a.Constructor.DeclaringType.FullName = cmaName then 
#endif
                    let args = a.ConstructorArguments
                    let flags = 
                         match args.Count  with 
                         | 1 -> ((args.[0].Value :?> SourceConstructFlags), 0, 0)
                         | 2 -> ((args.[0].Value :?> SourceConstructFlags), (args.[1].Value :?> int), 0)
                         | 3 -> ((args.[0].Value :?> SourceConstructFlags), (args.[1].Value :?> int), (args.[2].Value :?> int))
                         | _ -> (enum 0, 0, 0)
                    res <- Some flags
            res

    let findCompilationMappingAttributeFromData attrs =
      match tryFindCompilationMappingAttributeFromData attrs with
      | None -> failwith "no compilation mapping attribute"
      | Some a -> a

    let tryFindCompilationMappingAttributeFromType       (typ:Type)        = 
#if NET_CORE
#else
        let assem = typ.Assembly
        if assem <> null && assem.ReflectionOnly then 
           tryFindCompilationMappingAttributeFromData ( typ.GetCustomAttributesData())
        else
#endif
           tryFindCompilationMappingAttribute ( typ.GetCustomAttributes (typeof<CompilationMappingAttribute>,false))

    let tryFindCompilationMappingAttributeFromMemberInfo (info:MemberInfo) = 
#if NET_CORE
        tryFindCompilationMappingAttribute (info.GetCustomAttributes (typeof<CompilationMappingAttribute>,false) |> Seq.toArray)
#else
        let assem = info.DeclaringType.Assembly
        if assem <> null && assem.ReflectionOnly then 
           tryFindCompilationMappingAttributeFromData (info.GetCustomAttributesData())
        else
           tryFindCompilationMappingAttribute (info.GetCustomAttributes (typeof<CompilationMappingAttribute>,false))
#endif

    let    findCompilationMappingAttributeFromMemberInfo (info:MemberInfo) =    
#if NET_CORE
            findCompilationMappingAttribute (info.GetCustomAttributes (typeof<CompilationMappingAttribute>,false) |> Seq.toArray)
#else
        let assem = info.DeclaringType.Assembly
        if assem <> null && assem.ReflectionOnly then 
            findCompilationMappingAttributeFromData (info.GetCustomAttributesData())
        else
            findCompilationMappingAttribute (info.GetCustomAttributes (typeof<CompilationMappingAttribute>,false))
#endif

#endif

    let sequenceNumberOfMember          (x: MemberInfo) = let (_,n,_) = findCompilationMappingAttributeFromMemberInfo x in n
    let variantNumberOfMember           (x: MemberInfo) = let (_,_,vn) = findCompilationMappingAttributeFromMemberInfo x in vn

    let sortFreshArray f arr = Array.sortInPlaceWith f arr; arr

    let isFieldProperty (prop : PropertyInfo) =
        match tryFindCompilationMappingAttributeFromMemberInfo(prop) with
        | None -> false
        | Some (flags,_n,_vn) -> (flags &&& SourceConstructFlags.KindMask) = SourceConstructFlags.Field

    let allInstance  (ps : PropertyInfo[]) = (ps, false)
    let allStatic  (ps : PropertyInfo[]) = (ps, true)

    let tryFindSourceConstructFlagsOfType (typ:Type) = 
      match tryFindCompilationMappingAttributeFromType typ with 
      | None -> None
      | Some (flags,_n,_vn) -> Some flags

    //-----------------------------------------------------------------
    // UNION DECOMPILATION
    

    // Get the type where the type definitions are stored
    let getUnionCasesTyp (typ: Type, _bindingFlags) = 
#if CASES_IN_NESTED_CLASS
       let casesTyp = typ.GetNestedType("Cases", bindingFlags)
       if casesTyp.IsGenericTypeDefinition then casesTyp.MakeGenericType(typ.GetGenericArguments())
       else casesTyp
#else
       typ
#endif
            
    let getUnionTypeTagNameMap (typ:Type,bindingFlags) = 
        let enumTyp = typ.GetNestedType("Tags", bindingFlags)
        // Unions with a singleton case do not get a Tags type (since there is only one tag), hence enumTyp may be null in this case
        match enumTyp with
        | null -> 
            typ.GetMethods(staticMethodFlags ||| bindingFlags) 
            |> Array.choose (fun minfo -> 
                match tryFindCompilationMappingAttributeFromMemberInfo(minfo) with
                | None -> None
                | Some (flags,n,_vn) -> 
                    if (flags &&& SourceConstructFlags.KindMask) = SourceConstructFlags.UnionCase then 
                        let nm = minfo.Name 
                        // chop "get_" or  "New" off the front 
                        let nm = 
                            if not (isListType typ) && not (isOptionType typ) then 
                                if   nm.Length > 4 && nm.[0..3] = "get_" then nm.[4..] 
                                elif nm.Length > 3 && nm.[0..2] = "New" then nm.[3..]
                                else nm
                            else nm
                        Some (n, nm)
                    else
                        None) 
        | _ -> 
            enumTyp.GetFields(staticFieldFlags ||| bindingFlags) 
            |> Array.filter (fun (f:FieldInfo) -> f.IsStatic && f.IsLiteral) 
            |> sortFreshArray (fun f1 f2 -> compare (f1.GetValue(null) :?> int) (f2.GetValue(null) :?> int))
            |> Array.map (fun tagfield -> (tagfield.GetValue(null) :?> int),tagfield.Name)

    let getUnionCaseTyp (typ: Type, tag: int, bindingFlags) = 
        let tagFields = getUnionTypeTagNameMap(typ,bindingFlags)
        let tagField = tagFields |> Array.pick (fun (i,f) -> if i = tag then Some f else None)
        if tagFields.Length = 1 then 
            typ
        else 
            let casesTyp = getUnionCasesTyp (typ, bindingFlags)
            let caseTyp = casesTyp.GetNestedType(tagField, bindingFlags) // if this is null then the union is nullary
            match caseTyp with 
            | null -> null
            | _ when caseTyp.IsGenericTypeDefinition -> caseTyp.MakeGenericType(casesTyp.GetGenericArguments())
            | _ -> caseTyp

    let getUnionTagConverter (typ:Type,bindingFlags) = 
        if isOptionType typ then (fun tag -> match tag with 0 -> "None" | 1 -> "Some" | _ -> invalidArg "tag" (SR.GetString(SR.outOfRange)))
        elif isListType typ then (fun tag -> match tag with  0 -> "Empty" | 1 -> "Cons" | _ -> invalidArg "tag" (SR.GetString(SR.outOfRange)))
        else 
          let tagfieldmap = getUnionTypeTagNameMap (typ,bindingFlags) |> Map.ofSeq
          (fun tag -> tagfieldmap.[tag])

    let isUnionType (typ:Type,bindingFlags:BindingFlags) = 
        isOptionType typ || 
        isListType typ || 
        match tryFindSourceConstructFlagsOfType(typ) with 
        | None -> false
        | Some(flags) ->
          (flags &&& SourceConstructFlags.KindMask) = SourceConstructFlags.SumType &&
          // We see private representations only if BindingFlags.NonPublic is set
          (if (flags &&& SourceConstructFlags.NonPublicRepresentation) <> enum(0) then 
              (bindingFlags &&& BindingFlags.NonPublic) <> enum(0)
           else 
              true)

    // Check the base type - if it is also an F# type then
    // for the moment we know it is a Discriminated Union
    let isConstructorRepr (typ:Type,bindingFlags:BindingFlags) = 
        let rec get (typ:Type) = isUnionType (typ,bindingFlags) || match typ.BaseType with null -> false | b -> get b
        get typ 

    let unionTypeOfUnionCaseType (typ:Type,bindingFlags) = 
        let rec get (typ:Type) = if isUnionType (typ,bindingFlags) then typ else match typ.BaseType with null -> typ | b -> get b
        get typ 
                   
    let swap (x,y) = (y,x)

    let fieldsPropsOfUnionCase(typ:Type, tag:int, bindingFlags) =
        if isOptionType typ then 
            match tag with 
            | 0 (* None *) -> getInstancePropertyInfos (typ,[| |],bindingFlags) 
            | 1 (* Some *) -> getInstancePropertyInfos (typ,[| "Value" |] ,bindingFlags) 
            | _ -> failwith "fieldsPropsOfUnionCase"
        elif isListType typ then 
            match tag with 
            | 0 (* Nil *)  -> getInstancePropertyInfos (typ,[| |],bindingFlags) 
            | 1 (* Cons *) -> getInstancePropertyInfos (typ,[| "Head"; "Tail" |],bindingFlags) 
            | _ -> failwith "fieldsPropsOfUnionCase"
        else
            // Lookup the type holding the fields for the union case
            let caseTyp = getUnionCaseTyp (typ, tag, bindingFlags)
            match caseTyp with 
            | null ->  [| |]
            | _ ->  caseTyp.GetProperties(instancePropertyFlags ||| bindingFlags) 
                    |> Array.filter isFieldProperty
                    |> Array.filter (fun prop -> variantNumberOfMember prop = tag)
                    |> sortFreshArray (fun p1 p2 -> compare (sequenceNumberOfMember p1) (sequenceNumberOfMember p2))
                

    let getUnionCaseRecordReader (typ:Type,tag:int,bindingFlags) = 
        let props = fieldsPropsOfUnionCase(typ,tag,bindingFlags)
#if FX_ATLEAST_PORTABLE
        (fun (obj:obj) -> props |> Array.map (fun prop -> prop.GetValue(obj,null)))
#else        
        (fun (obj:obj) -> props |> Array.map (fun prop -> prop.GetValue(obj,bindingFlags,null,null,null)))
#endif
    let getUnionTagReader (typ:Type,bindingFlags) : (obj -> int) = 
        if isOptionType typ then 
            (fun (obj:obj) -> match obj with null -> 0 | _ -> 1)
        else
            let tagMap = getUnionTypeTagNameMap (typ, bindingFlags)
            if tagMap.Length <= 1 then 
                (fun (_obj:obj) -> 0)
            else   
                match getInstancePropertyReader (typ,"Tag",bindingFlags) with
                | Some reader -> (fun (obj:obj) -> reader obj :?> int)
                | None -> 
                    (fun (obj:obj) -> 
#if FX_ATLEAST_PORTABLE
                        let m2b = typ.GetMethod("GetTag", [| typ |])
#else                    
                        let m2b = typ.GetMethod("GetTag", BindingFlags.Static ||| bindingFlags, null, [| typ |], null)
#endif                        
                        m2b.Invoke(null, [|obj|]) :?> int)
        
    let getUnionTagMemberInfo (typ:Type,bindingFlags) = 
        match getInstancePropertyInfo (typ,"Tag",bindingFlags) with
#if FX_ATLEAST_PORTABLE
        | null -> (typ.GetMethod("GetTag") :> MemberInfo)
#else        
        | null -> (typ.GetMethod("GetTag",BindingFlags.Static ||| bindingFlags) :> MemberInfo)
#endif        
        | info -> (info :> MemberInfo)

    let isUnionCaseNullary (typ:Type, tag:int, bindingFlags) = 
        let props = fieldsPropsOfUnionCase(typ, tag, bindingFlags) 
        emptyArray props

    let getUnionCaseConstructorMethod (typ:Type,tag:int,bindingFlags) = 
        let constrname = getUnionTagConverter (typ,bindingFlags) tag 
        let methname = 
            if isUnionCaseNullary (typ, tag, bindingFlags) then "get_"+constrname 
            elif isListType typ || isOptionType typ then constrname
            else "New"+constrname 
        match typ.GetMethod(methname, BindingFlags.Static  ||| bindingFlags) with
        | null -> raise <| System.InvalidOperationException (SR.GetString1(SR.constructorForUnionCaseNotFound, methname))
        | meth -> meth

    let getUnionCaseConstructor (typ:Type,tag:int,bindingFlags) = 
        let meth = getUnionCaseConstructorMethod (typ,tag,bindingFlags)
        (fun args -> 
#if FX_ATLEAST_PORTABLE
            meth.Invoke(null,args))
#else        
            meth.Invoke(null,BindingFlags.Static ||| BindingFlags.InvokeMethod ||| bindingFlags,null,args,null))
#endif
    let checkUnionType(unionType,bindingFlags) =
        checkNonNull "unionType" unionType;
        if not (isUnionType (unionType,bindingFlags)) then 
            if isUnionType (unionType,bindingFlags ||| BindingFlags.NonPublic) then 
                invalidArg "unionType" (SR.GetString1(SR.privateUnionType, unionType.FullName))
            else
                invalidArg "unionType" (SR.GetString1(SR.notAUnionType, unionType.FullName))
    let emptyObjArray : obj[] = [| |]

    //-----------------------------------------------------------------
    // TUPLE DECOMPILATION
    
    let tuple1 = typedefof<Tuple<obj>>
    let tuple2 = typedefof<obj * obj>
    let tuple3 = typedefof<obj * obj * obj>
    let tuple4 = typedefof<obj * obj * obj * obj>
    let tuple5 = typedefof<obj * obj * obj * obj * obj>
    let tuple6 = typedefof<obj * obj * obj * obj * obj * obj>
    let tuple7 = typedefof<obj * obj * obj * obj * obj * obj * obj>
    let tuple8 = typedefof<obj * obj * obj * obj * obj * obj * obj * obj>

    let isTuple1Type typ = equivHeadTypes typ tuple1
    let isTuple2Type typ = equivHeadTypes typ tuple2
    let isTuple3Type typ = equivHeadTypes typ tuple3
    let isTuple4Type typ = equivHeadTypes typ tuple4
    let isTuple5Type typ = equivHeadTypes typ tuple5
    let isTuple6Type typ = equivHeadTypes typ tuple6
    let isTuple7Type typ = equivHeadTypes typ tuple7
    let isTuple8Type typ = equivHeadTypes typ tuple8

    let isTupleType typ = 
           isTuple1Type typ
        || isTuple2Type typ
        || isTuple3Type typ 
        || isTuple4Type typ 
        || isTuple5Type typ 
        || isTuple6Type typ 
        || isTuple7Type typ 
        || isTuple8Type typ

    let maxTuple = 8
    // Which field holds the nested tuple?
    let tupleEncField = maxTuple-1
    
    let rec mkTupleType (tys: Type[]) = 
        match tys.Length with 
        | 1 -> tuple1.MakeGenericType(tys)
        | 2 -> tuple2.MakeGenericType(tys)
        | 3 -> tuple3.MakeGenericType(tys)
        | 4 -> tuple4.MakeGenericType(tys)
        | 5 -> tuple5.MakeGenericType(tys)
        | 6 -> tuple6.MakeGenericType(tys)
        | 7 -> tuple7.MakeGenericType(tys)
        | n when n >= maxTuple -> 
            let tysA = tys.[0..tupleEncField-1]
            let tysB = tys.[maxTuple-1..]
            let tyB = mkTupleType tysB
            tuple8.MakeGenericType(Array.append tysA [| tyB |])
        | _ -> invalidArg "tys" (SR.GetString(SR.invalidTupleTypes))


    let rec getTupleTypeInfo    (typ:Type) = 
      if not (isTupleType (typ) ) then invalidArg "typ" (SR.GetString1(SR.notATupleType, typ.FullName));
      let tyargs = typ.GetGenericArguments()
      if tyargs.Length = maxTuple then 
          let tysA = tyargs.[0..tupleEncField-1]
          let tyB = tyargs.[tupleEncField]
          Array.append tysA (getTupleTypeInfo tyB)
      else 
          tyargs

    let orderTupleProperties (props:PropertyInfo[]) =
        // The tuple properties are of the form:
        //   Item1
        //   ..
        //   Item1, Item2, ..., Item<maxTuple-1>
        //   Item1, Item2, ..., Item<maxTuple-1>, Rest
        // The PropertyInfo may not come back in order, so ensure ordering here.
#if FX_ATLEAST_PORTABLE
#else
        assert(maxTuple < 10) // Alphasort will only works for upto 9 items: Item1, Item10, Item2, Item3, ..., Item9, Rest
#endif
        let props = props |> Array.sortBy (fun p -> p.Name) // they are not always in alphabetic order
#if FX_ATLEAST_PORTABLE  
#else      
        assert(props.Length <= maxTuple)
        assert(let haveNames   = props |> Array.map (fun p -> p.Name)
               let expectNames = Array.init props.Length (fun i -> let j = i+1 // index j = 1,2,..,props.Length <= maxTuple
                                                                   if   j<maxTuple then "Item" + string j
                                                                   elif j=maxTuple then "Rest"
                                                                   else (assert false; "")) // dead code under prior assert, props.Length <= maxTuple
               haveNames = expectNames)
#endif               
        props
            
    let getTupleConstructorMethod(typ:Type,bindingFlags) =
          let props = typ.GetProperties() |> orderTupleProperties
#if FX_ATLEAST_PORTABLE
          let ctor = typ.GetConstructor(props |> Array.map (fun p -> p.PropertyType))
          ignore bindingFlags
#else          
          let ctor = typ.GetConstructor(BindingFlags.Instance ||| bindingFlags,null,props |> Array.map (fun p -> p.PropertyType),null)
#endif          
          match ctor with
          | null -> raise <| ArgumentException(SR.GetString1(SR.invalidTupleTypeConstructorNotDefined, typ.FullName))
          | _ -> ()
          ctor
        
    let getTupleCtor(typ:Type,bindingFlags) =
          let ctor = getTupleConstructorMethod(typ,bindingFlags)
          (fun (args:obj[]) ->
#if FX_ATLEAST_PORTABLE   
              ctor.Invoke(args))
#else
              ctor.Invoke(BindingFlags.InvokeMethod ||| BindingFlags.Instance ||| bindingFlags,null,args,null))
#endif              

    let rec getTupleReader (typ:Type) = 
        let etys = typ.GetGenericArguments() 
        // Get the reader for the outer tuple record
        let props = typ.GetProperties(instancePropertyFlags ||| BindingFlags.Public) |> orderTupleProperties
        let reader = (fun (obj:obj) -> props |> Array.map (fun prop -> prop.GetValue(obj,null)))
        if etys.Length < maxTuple 
        then reader
        else
            let tyBenc = etys.[tupleEncField]
            let reader2 = getTupleReader(tyBenc)
            (fun obj ->
                let directVals = reader obj
                let encVals = reader2 directVals.[tupleEncField]
                Array.append directVals.[0..tupleEncField-1] encVals)
                
    let rec getTupleConstructor (typ:Type) = 
        let etys = typ.GetGenericArguments() 
        let maker1 =  getTupleCtor (typ,BindingFlags.Public)
        if etys.Length < maxTuple 
        then maker1
        else
            let tyBenc = etys.[tupleEncField]
            let maker2 = getTupleConstructor(tyBenc)
            (fun (args:obj[]) ->
                let encVal = maker2 args.[tupleEncField..]
                maker1 (Array.append args.[0..tupleEncField-1] [| encVal |]))
                
    let getTupleConstructorInfo (typ:Type) = 
        let etys = typ.GetGenericArguments() 
        let maker1 =  getTupleConstructorMethod (typ,BindingFlags.Public)
        if etys.Length < maxTuple then
            maker1,None
        else
            maker1,Some(etys.[tupleEncField])

    let getTupleReaderInfo (typ:Type,index:int) =         
        if index < 0 then invalidArg "index" (SR.GetString2(SR.tupleIndexOutOfRange, typ.FullName, index.ToString()))
        let props = typ.GetProperties(instancePropertyFlags ||| BindingFlags.Public) |> orderTupleProperties
        let get index = 
            if index >= props.Length then invalidArg "index" (SR.GetString2(SR.tupleIndexOutOfRange, typ.FullName, index.ToString()))
            props.[index]
        
        if index < tupleEncField then
            get index, None  
        else
            let etys = typ.GetGenericArguments()
            get tupleEncField, Some(etys.[tupleEncField],index-(maxTuple-1))
            
      
    //-----------------------------------------------------------------
    // FUNCTION DECOMPILATION
    
      
    let getFunctionTypeInfo (typ:Type) =
      if not (isFunctionType typ) then invalidArg "typ" (SR.GetString1(SR.notAFunctionType, typ.FullName))
      let tyargs = typ.GetGenericArguments()
      tyargs.[0], tyargs.[1]

    //-----------------------------------------------------------------
    // MODULE DECOMPILATION
    
    let isModuleType (typ:Type) = 
      match tryFindSourceConstructFlagsOfType(typ) with 
      | None -> false 
      | Some(flags) -> 
        (flags &&& SourceConstructFlags.KindMask) = SourceConstructFlags.Module 

    let rec isClosureRepr typ = 
        isFunctionType typ || 
        (match typ.BaseType with null -> false | bty -> isClosureRepr bty) 

    //-----------------------------------------------------------------
    // RECORD DECOMPILATION
    
    let isRecordType (typ:Type,bindingFlags:BindingFlags) = 
      match tryFindSourceConstructFlagsOfType(typ) with 
      | None -> false 
      | Some(flags) ->
        (flags &&& SourceConstructFlags.KindMask) = SourceConstructFlags.RecordType &&
        // We see private representations only if BindingFlags.NonPublic is set
        (if (flags &&& SourceConstructFlags.NonPublicRepresentation) <> enum(0) then 
            (bindingFlags &&& BindingFlags.NonPublic) <> enum(0)
         else 
            true) &&
        not (isTupleType typ)

    let fieldPropsOfRecordType(typ:Type,bindingFlags) =
      typ.GetProperties(instancePropertyFlags ||| bindingFlags) 
      |> Array.filter isFieldProperty
      |> sortFreshArray (fun p1 p2 -> compare (sequenceNumberOfMember p1) (sequenceNumberOfMember p2))

    let recdDescOfProps props = 
       props |> Array.toList |> List.map (fun (p:PropertyInfo) -> p.Name, p.PropertyType) 

    let getRecd obj (props:PropertyInfo[]) = 
        props |> Array.map (fun prop -> prop.GetValue(obj,null))

    let getRecordReader(typ:Type,bindingFlags) = 
        let props = fieldPropsOfRecordType(typ,bindingFlags)
        (fun (obj:obj) -> props |> Array.map (fun prop -> prop.GetValue(obj,null)))

    let getRecordConstructorMethod(typ:Type,bindingFlags) = 
        let props = fieldPropsOfRecordType(typ,bindingFlags)
#if FX_ATLEAST_PORTABLE
        let ctor = typ.GetConstructor(props |> Array.map (fun p -> p.PropertyType))
#else        
        let ctor = typ.GetConstructor(BindingFlags.Instance ||| bindingFlags,null,props |> Array.map (fun p -> p.PropertyType),null)
#endif        
        match ctor with
        | null -> raise <| ArgumentException(SR.GetString1(SR.invalidRecordTypeConstructorNotDefined, typ.FullName))
        | _ -> ()
        ctor

    let getRecordConstructor(typ:Type,bindingFlags) = 
        let ctor = getRecordConstructorMethod(typ,bindingFlags)
        (fun (args:obj[]) -> 
#if FX_ATLEAST_PORTABLE
            ctor.Invoke(args))
#else        
            ctor.Invoke(BindingFlags.InvokeMethod  ||| BindingFlags.Instance ||| bindingFlags,null,args,null))
#endif            

    //-----------------------------------------------------------------
    // EXCEPTION DECOMPILATION
    

    // Check the base type - if it is also an F# type then
    // for the moment we know it is a Discriminated Union
    let isExceptionRepr (typ:Type,bindingFlags) = 
        match tryFindSourceConstructFlagsOfType(typ) with 
        | None -> false 
        | Some(flags) -> 
          ((flags &&& SourceConstructFlags.KindMask) = SourceConstructFlags.Exception) &&
          // We see private representations only if BindingFlags.NonPublic is set
          (if (flags &&& SourceConstructFlags.NonPublicRepresentation) <> enum(0) then 
              (bindingFlags &&& BindingFlags.NonPublic) <> enum(0)
           else 
              true)


    let getTypeOfReprType (typ:Type,bindingFlags) = 
        if isExceptionRepr(typ,bindingFlags) then typ.BaseType
        elif isConstructorRepr(typ,bindingFlags) then unionTypeOfUnionCaseType(typ,bindingFlags)
        elif isClosureRepr(typ) then 
          let rec get (typ:Type) = if isFunctionType typ then typ else match typ.BaseType with null -> typ | b -> get b
          get typ 
        else typ


    //-----------------------------------------------------------------
    // CHECKING ROUTINES

    let checkExnType (exceptionType, bindingFlags) =
        if not (isExceptionRepr (exceptionType,bindingFlags)) then 
            if isExceptionRepr (exceptionType,bindingFlags ||| BindingFlags.NonPublic) then 
                invalidArg "exceptionType" (SR.GetString1(SR.privateExceptionType, exceptionType.FullName))
            else
                invalidArg "exceptionType" (SR.GetString1(SR.notAnExceptionType, exceptionType.FullName))
           
    let checkRecordType(argName,recordType,bindingFlags) =
        checkNonNull argName recordType;
        if not (isRecordType (recordType,bindingFlags) ) then 
            if isRecordType (recordType,bindingFlags ||| BindingFlags.NonPublic) then 
                invalidArg argName (SR.GetString1(SR.privateRecordType, recordType.FullName))
            else
                invalidArg argName (SR.GetString1(SR.notARecordType, recordType.FullName))
        
    let checkTupleType(argName,tupleType) =
        checkNonNull argName tupleType;
        if not (isTupleType tupleType) then invalidArg argName (SR.GetString1(SR.notATupleType, tupleType.FullName))
        
[<Sealed>]
type UnionCaseInfo(typ: System.Type, tag:int) =
    // Cache the tag -> name map
    let mutable names = None
    let getMethInfo() = Impl.getUnionCaseConstructorMethod (typ,tag,BindingFlags.Public ||| BindingFlags.NonPublic) 
    member x.Name = 
        match names with 
        | None -> (let conv = Impl.getUnionTagConverter (typ,BindingFlags.Public ||| BindingFlags.NonPublic) in names <- Some conv; conv tag)
        | Some conv -> conv tag
        
    member x.DeclaringType = typ
    //member x.CustomAttributes = failwith<obj[]> "nyi"
    member x.GetFields() = 
        let props = Impl.fieldsPropsOfUnionCase(typ,tag,BindingFlags.Public ||| BindingFlags.NonPublic) 
        props

#if NET_CORE
    member x.GetCustomAttributes() = getMethInfo().GetCustomAttributes(false) |> Seq.map box |> Seq.toArray
    
    member x.GetCustomAttributes(attributeType) = getMethInfo().GetCustomAttributes(attributeType,false) |> Seq.map box |> Seq.toArray
#else
    member x.GetCustomAttributes() = getMethInfo().GetCustomAttributes(false)
    
    member x.GetCustomAttributes(attributeType) = getMethInfo().GetCustomAttributes(attributeType,false)
#endif

#if FX_NO_CUSTOMATTRIBUTEDATA
#else
#if NET_CORE
    member x.GetCustomAttributesData() = getMethInfo().GetCustomAttributesData() |> Seq.toArray :> System.Collections.Generic.IList<CustomAttributeData>
#else
    member x.GetCustomAttributesData() = getMethInfo().GetCustomAttributesData()
#endif
#endif    
    member x.Tag = tag
    override x.ToString() = typ.Name + "." + x.Name
    override x.GetHashCode() = typ.GetHashCode() + tag
    override x.Equals(obj:obj) = 
        match obj with 
        | :? UnionCaseInfo as uci -> uci.DeclaringType = typ && uci.Tag = tag
        | _ -> false
    

[<AbstractClass; Sealed>]
type FSharpType = 

    static member IsTuple(typ:Type) =  
        Impl.checkNonNull "typ" typ;
        Impl.isTupleType typ

    static member IsRecord(typ:Type,?bindingFlags) =  
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkNonNull "typ" typ;
        Impl.isRecordType (typ,bindingFlags)

    static member IsUnion(typ:Type,?bindingFlags) =  
        Impl.checkNonNull "typ" typ;
        let typ = Impl.getTypeOfReprType (typ ,BindingFlags.Public ||| BindingFlags.NonPublic)
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.isUnionType (typ,bindingFlags)

    static member IsFunction(typ:Type) =  
        Impl.checkNonNull "typ" typ;
        let typ = Impl.getTypeOfReprType (typ ,BindingFlags.Public ||| BindingFlags.NonPublic)
        Impl.isFunctionType typ

    static member IsModule(typ:Type) =  
        Impl.checkNonNull "typ" typ;
        Impl.isModuleType typ

    static member MakeFunctionType(domain:Type,range:Type) = 
        Impl.checkNonNull "domain" domain;
        Impl.checkNonNull "range" range;
        Impl.func.MakeGenericType [| domain; range |]

    static member MakeTupleType(types:Type[]) =  
        Impl.checkNonNull "types" types;
        if types |> Array.exists (function null -> true | _ -> false) then 
             invalidArg "types" (SR.GetString(SR.nullsNotAllowedInArray))
        Impl.mkTupleType types

    static member GetTupleElements(tupleType:Type) =
        Impl.checkTupleType("tupleType",tupleType);
        Impl.getTupleTypeInfo tupleType

    static member GetFunctionElements(functionType:Type) =
        Impl.checkNonNull "functionType" functionType;
        let functionType = Impl.getTypeOfReprType (functionType ,BindingFlags.Public ||| BindingFlags.NonPublic)
        Impl.getFunctionTypeInfo functionType

    static member GetRecordFields(recordType:Type,?bindingFlags) =
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkRecordType("recordType",recordType,bindingFlags);
        Impl.fieldPropsOfRecordType(recordType,bindingFlags) 

    static member GetUnionCases (unionType:Type,?bindingFlags) = 
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkNonNull "unionType" unionType;
        let unionType = Impl.getTypeOfReprType (unionType ,bindingFlags)
        Impl.checkUnionType(unionType,bindingFlags);
        Impl.getUnionTypeTagNameMap(unionType,bindingFlags) |> Array.mapi (fun i _ -> UnionCaseInfo(unionType,i))

    static member IsExceptionRepresentation(exceptionType:Type, ?bindingFlags) = 
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkNonNull "exceptionType" exceptionType;
        Impl.isExceptionRepr(exceptionType,bindingFlags)

    static member GetExceptionFields(exceptionType:Type, ?bindingFlags) = 
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkNonNull "exceptionType" exceptionType;
        Impl.checkExnType(exceptionType,bindingFlags);
        Impl.fieldPropsOfRecordType (exceptionType,bindingFlags) 

#if NET_CORE
    static member IsRecord(typ) = FSharpType.IsRecord(typ,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member IsUnion(typ) = FSharpType.IsUnion(typ,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member GetRecordFields(recordType) = FSharpType.GetRecordFields(recordType,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member GetUnionCases(unionType) = FSharpType.GetUnionCases(unionType,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member IsExceptionRepresentation(exceptionType) = FSharpType.IsExceptionRepresentation(exceptionType,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member GetExceptionFields(exceptionType) = FSharpType.GetExceptionFields(exceptionType,BindingFlags.Public ||| BindingFlags.NonPublic)
#endif

type DynamicFunction<'T1,'T2>() =
    inherit FSharpFunc<obj -> obj, obj>()
    override x.Invoke(impl: obj -> obj) : obj = 
        box<('T1 -> 'T2)> (fun inp -> unbox<'T2>(impl (box<'T1>(inp))))

[<AbstractClass; Sealed>]
type FSharpValue = 

    static member MakeRecord(recordType:Type,args,?bindingFlags) = 
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkRecordType("recordType",recordType,bindingFlags);
        Impl.getRecordConstructor (recordType,bindingFlags) args

    static member GetRecordField(record:obj,info:PropertyInfo) =
        Impl.checkNonNull "info" info;
        Impl.checkNonNull "record" record;
        let reprty = record.GetType() 
        if not (Impl.isRecordType(reprty,BindingFlags.Public ||| BindingFlags.NonPublic)) then invalidArg "record" (SR.GetString(SR.objIsNotARecord));
        info.GetValue(record,null)

    static member GetRecordFields(record:obj,?bindingFlags) =
        Impl.checkNonNull "record" record;
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        let typ = record.GetType() 
        if not (Impl.isRecordType(typ,bindingFlags)) then invalidArg "record" (SR.GetString(SR.objIsNotARecord));
        Impl.getRecordReader (typ,bindingFlags) record

    static member PreComputeRecordFieldReader(info:PropertyInfo) = 
        Impl.checkNonNull "info" info;
        (fun (obj:obj) -> info.GetValue(obj,null))

    static member PreComputeRecordReader(recordType:Type,?bindingFlags) : (obj -> obj[]) =  
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkRecordType("recordType",recordType,bindingFlags);
        Impl.getRecordReader (recordType,bindingFlags)

    static member PreComputeRecordConstructor(recordType:Type,?bindingFlags) = 
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkRecordType("recordType",recordType,bindingFlags);
        Impl.getRecordConstructor (recordType,bindingFlags)

    static member PreComputeRecordConstructorInfo(recordType:Type, ?bindingFlags) =
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkRecordType("recordType",recordType,bindingFlags);
        Impl.getRecordConstructorMethod(recordType,bindingFlags)

    static member MakeFunction(functionType:Type,implementation:(obj->obj)) = 
        Impl.checkNonNull "functionType" functionType;
        if not (Impl.isFunctionType functionType) then invalidArg "functionType" (SR.GetString1(SR.notAFunctionType, functionType.FullName));
        Impl.checkNonNull "implementation" implementation;
        let domain,range = Impl.getFunctionTypeInfo functionType
        let dynCloMakerTy = typedefof<DynamicFunction<obj,obj>>
        let saverTy = dynCloMakerTy.MakeGenericType [| domain; range |]
        let o = Activator.CreateInstance(saverTy)
        let (f : (obj -> obj) -> obj) = downcast o
        f implementation

    static member MakeTuple(tupleElements: obj[],tupleType:Type) =
        Impl.checkNonNull "tupleElements" tupleElements;
        Impl.checkTupleType("tupleType",tupleType) 
        Impl.getTupleConstructor tupleType tupleElements
    
    static member GetTupleFields(tuple:obj) = // argument name(s) used in error message
        Impl.checkNonNull "tuple" tuple;
        let typ = tuple.GetType() 
        if not (Impl.isTupleType typ ) then invalidArg "tuple" (SR.GetString1(SR.notATupleType, tuple.GetType().FullName));
        Impl.getTupleReader typ tuple

    static member GetTupleField(tuple:obj,index:int) = // argument name(s) used in error message
        Impl.checkNonNull "tuple" tuple;
        let typ = tuple.GetType() 
        if not (Impl.isTupleType typ ) then invalidArg "tuple" (SR.GetString1(SR.notATupleType, tuple.GetType().FullName));
        let fields = Impl.getTupleReader typ tuple
        if index < 0 || index >= fields.Length then invalidArg "index" (SR.GetString2(SR.tupleIndexOutOfRange, tuple.GetType().FullName, index.ToString()));
        fields.[index]
    
    static member PreComputeTupleReader(tupleType:Type) : (obj -> obj[])  =
        Impl.checkTupleType("tupleType",tupleType) 
        Impl.getTupleReader tupleType
    
    static member PreComputeTuplePropertyInfo(tupleType:Type,index:int) =
        Impl.checkTupleType("tupleType",tupleType) 
        Impl.getTupleReaderInfo (tupleType,index)
    
    static member PreComputeTupleConstructor(tupleType:Type) = 
        Impl.checkTupleType("tupleType",tupleType) 
        Impl.getTupleConstructor tupleType

    static member PreComputeTupleConstructorInfo(tupleType:Type) =
        Impl.checkTupleType("tupleType",tupleType) 
        Impl.getTupleConstructorInfo (tupleType) 

    static member MakeUnion(unionCase:UnionCaseInfo,args: obj [],?bindingFlags) = 
        Impl.checkNonNull "unionCase" unionCase;
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.getUnionCaseConstructor (unionCase.DeclaringType,unionCase.Tag,bindingFlags) args

    static member PreComputeUnionConstructor (unionCase:UnionCaseInfo,?bindingFlags) = 
        Impl.checkNonNull "unionCase" unionCase;
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.getUnionCaseConstructor (unionCase.DeclaringType,unionCase.Tag,bindingFlags)

    static member PreComputeUnionConstructorInfo(unionCase:UnionCaseInfo, ?bindingFlags) =
        Impl.checkNonNull "unionCase" unionCase;
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.getUnionCaseConstructorMethod (unionCase.DeclaringType,unionCase.Tag,bindingFlags) 

    static member GetUnionFields(obj:obj,unionType:Type,?bindingFlags) = 
        let ensureType (typ:Type,obj:obj) = 
                match typ with 
                | null -> 
                    match obj with 
                    | null -> invalidArg "obj" (SR.GetString(SR.objIsNullAndNoType))
                    | _ -> obj.GetType()
                | _ -> typ 
        //System.Console.WriteLine("typ1 = {0}",box unionType)
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        let unionType = ensureType(unionType,obj) 
        //System.Console.WriteLine("typ2 = {0}",box unionType)
        Impl.checkNonNull "unionType" unionType;
        let unionType = Impl.getTypeOfReprType (unionType ,bindingFlags)
        //System.Console.WriteLine("typ3 = {0}",box unionType)
        Impl.checkUnionType(unionType,bindingFlags);
        let tag = Impl.getUnionTagReader (unionType,bindingFlags) obj
        let flds = Impl.getUnionCaseRecordReader (unionType,tag,bindingFlags) obj 
        UnionCaseInfo(unionType,tag), flds

    static member PreComputeUnionTagReader(unionType: Type,?bindingFlags) : (obj -> int) = 
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkNonNull "unionType" unionType;
        let unionType = Impl.getTypeOfReprType (unionType ,bindingFlags)
        Impl.checkUnionType(unionType,bindingFlags);
        Impl.getUnionTagReader (unionType ,bindingFlags)

    static member PreComputeUnionTagMemberInfo(unionType: Type,?bindingFlags) = 
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        Impl.checkNonNull "unionType" unionType;
        let unionType = Impl.getTypeOfReprType (unionType ,bindingFlags)
        Impl.checkUnionType(unionType,bindingFlags);
        Impl.getUnionTagMemberInfo(unionType ,bindingFlags)

    static member PreComputeUnionReader(unionCase: UnionCaseInfo,?bindingFlags) : (obj -> obj[])  = 
        Impl.checkNonNull "unionCase" unionCase;
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        let typ = unionCase.DeclaringType 
        Impl.getUnionCaseRecordReader (typ,unionCase.Tag,bindingFlags) 
    

    static member GetExceptionFields(exn:obj, ?bindingFlags) = 
        Impl.checkNonNull "exn" exn;
        let bindingFlags = defaultArg bindingFlags BindingFlags.Public 
        let typ = exn.GetType() 
        Impl.checkExnType(typ,bindingFlags);
        Impl.getRecordReader (typ,bindingFlags) exn


#if NET_CORE
    static member MakeRecord(recordType,args) = FSharpValue.MakeRecord(recordType,args,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member MakeUnion(unionCase,args) = FSharpValue.MakeUnion(unionCase,args,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member GetRecordFields(record:obj) = FSharpValue.GetRecordFields(record,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member PreComputeRecordReader(recordType) = FSharpValue.PreComputeRecordReader(recordType,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member PreComputeRecordConstructor(recordType) = FSharpValue.PreComputeRecordConstructor(recordType,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member PreComputeRecordConstructorInfo(recordType) = FSharpValue.PreComputeRecordConstructorInfo(recordType,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member PreComputeUnionConstructor(unionCase) = FSharpValue.PreComputeUnionConstructor(unionCase,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member PreComputeUnionConstructorInfo(unionCase) = FSharpValue.PreComputeUnionConstructorInfo(unionCase,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member GetUnionFields(obj:obj,unionType) = FSharpValue.GetUnionFields(obj,unionType,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member PreComputeUnionTagReader(unionType: Type) = FSharpValue.PreComputeUnionTagReader(unionType,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member PreComputeUnionTagMemberInfo(unionType) = FSharpValue.PreComputeUnionTagMemberInfo(unionType,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member PreComputeUnionReader(unionCase) = FSharpValue.PreComputeUnionReader(unionCase,BindingFlags.Public ||| BindingFlags.NonPublic)
    static member GetExceptionFields(exn:obj) = FSharpValue.GetExceptionFields(exn,BindingFlags.Public ||| BindingFlags.NonPublic)
#endif
