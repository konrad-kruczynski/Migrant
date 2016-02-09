/*
  Copyright (c) 2012 - 2016 Antmicro <www.antmicro.com>

  Authors:
   * Konrad Kruczynski (kkruczynski@antmicro.com)
   * Piotr Zierhoffer (pzierhoffer@antmicro.com)
   * Mateusz Holenko (mholenko@antmicro.com)

  Permission is hereby granted, free of charge, to any person obtaining
  a copy of this software and associated documentation files (the
  "Software"), to deal in the Software without restriction, including
  without limitation the rights to use, copy, modify, merge, publish,
  distribute, sublicense, and/or sell copies of the Software, and to
  permit persons to whom the Software is furnished to do so, subject to
  the following conditions:

  The above copyright notice and this permission notice shall be
  included in all copies or substantial portions of the Software.

  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
  EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
  MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
  NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
  LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
  OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
  WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Linq;
using System.Collections.Generic;
using System.Collections;
using Antmicro.Migrant.Hooks;
using Antmicro.Migrant.Utilities;
using Antmicro.Migrant.Generators;
using Antmicro.Migrant.VersionTolerance;
using Antmicro.Migrant.Customization;

namespace Antmicro.Migrant
{
    internal class ObjectReader
    {
        public ObjectReader(Stream stream, Serializer.ReadMethods readMethods, SwapList objectsForSurrogates = null, Action<object> postDeserializationCallback = null,
                            bool treatCollectionAsUserObject = false,
                            VersionToleranceLevel versionToleranceLevel = 0, bool useBuffering = true, bool disableStamping = false,
                            ReferencePreservation referencePreservation = ReferencePreservation.Preserve)
        {
            this.readMethods = readMethods;
            this.postDeserializationCallback = postDeserializationCallback;
            this.treatCollectionAsUserObject = treatCollectionAsUserObject;
            this.referencePreservation = referencePreservation;
            this.objectsForSurrogates = objectsForSurrogates ?? new SwapList();

            VersionToleranceLevel = versionToleranceLevel;
            types = new List<TypeDescriptor>();
            Methods = new IdentifiedElementsList<MethodDescriptor>(this);
            Assemblies = new IdentifiedElementsList<AssemblyDescriptor>(this);
            Modules = new IdentifiedElementsList<ModuleDescriptor>(this);
            postDeserializationHooks = new List<Action>();

            reader = new PrimitiveReader(stream, useBuffering);

            objectsWrittenInline = new HashSet<int>();
            surrogatesWhileReading = new OneToOneMap<int, object>();
            waitingList = new WaitCollection<int>();
            completedObjects = new Queue<int>();

            readTypeMethod = disableStamping ? (Func<TypeDescriptor>)ReadSimpleTypeDescriptor : ReadFullTypeDescriptor;
        }

        public void ReuseWithNewStream(Stream stream)
        {
            deserializedObjects.Clear();
            types.Clear();
            Methods.Clear();
            Assemblies.Clear();
            Modules.Clear();
            reader = new PrimitiveReader(stream, reader.IsBuffered);
        }

        public T ReadObject<T>()
        {
            if(soFarDeserialized != null)
            {
                deserializedObjects = new AutoResizingList<object>(soFarDeserialized.Length);
                for(var i = 0; i < soFarDeserialized.Length; i++)
                {
                    deserializedObjects[i] = soFarDeserialized[i].Target;
                }
            }
            if(deserializedObjects == null)
            {
                deserializedObjects = new AutoResizingList<object>(InitialCapacity);
            }

            objectsWrittenInline.Clear();
            var before = deserializedObjects.Count;
            var theRefId = ReadAndTouchReference();
            if(theRefId == before)
            {
                var after = deserializedObjects.Count;
                if(before < after - objectsWrittenInline.Count)
                {
                    do
                    {
                        var refId = reader.ReadInt32();
                        var type = GetObjectByReferenceId(refId).GetType();
                        readMethods.readMethodsProvider.GetOrCreate(type)(this, type, refId);
                    }
                    while(waitingList.Count > 0);
                }
            }

            var obj = deserializedObjects[theRefId];
            if(!(obj is T))
            {
                throw new InvalidDataException(
                    string.Format("Type {0} requested to be deserialized, however type {1} encountered in the stream.",
                        typeof(T), obj.GetType()));
            }

            PrepareForNextRead();
            foreach(var hook in postDeserializationHooks)
            {
                hook();
            }
            return (T)obj;
        }

        public void Flush()
        {
            reader.Dispose();
        }

        private void PrepareForNextRead()
        {
            if(referencePreservation == ReferencePreservation.UseWeakReference)
            {
                soFarDeserialized = new WeakReference[deserializedObjects.Count];
                for(var i = 0; i < soFarDeserialized.Length; i++)
                {
                    soFarDeserialized[i] = new WeakReference(deserializedObjects[i]);
                }
            }
            if(referencePreservation != ReferencePreservation.Preserve)
            {
                deserializedObjects = null;
            }
        }

        internal static bool HasSpecialReadMethod(Type type)
        {
            return type == typeof(string) || typeof(ISpeciallySerializable).IsAssignableFrom(type) || Helpers.IsTransient(type);
        }

        /// <summary>
        /// Reads the object using reflection.
        /// 
        /// REMARK: this method is not thread-safe
        /// </summary>
        /// <param name="objectReader">Object reader</param>
        /// <param name="actualType">Type of object to deserialize</param>
        /// <param name="objectId">Identifier of object to deserialize</param>
        internal static void ReadObjectInnerUsingReflection(ObjectReader objectReader, Type actualType, int objectId)
        {
            objectReader.TryTouchObject(actualType, objectId);
            objectReader.ResetMaxAskedReferenceId();

            switch(GetCreationWay(actualType, objectReader.treatCollectionAsUserObject))
            {
            case CreationWay.Null:
                objectReader.ReadNotPrecreated(actualType, objectId);
                break;
            case CreationWay.DefaultCtor:
                objectReader.UpdateElements(actualType, objectId);
                break;
            case CreationWay.Uninitialized:
                objectReader.UpdateFields(actualType, objectReader.GetObjectByReferenceId(objectId));
                break;
            }
            var obj = objectReader.GetObjectByReferenceId(objectId);
            if(obj == null)
            {
                // it can happen if we deserialize delegate with empty invocation list
                return;
            }

            objectReader.UpdateWaitingList(objectId);
        }

        internal void UpdateWaitingList(int objectId)
        {
            var idOfObjectToWaitFor = MaxAskedReferenceId;

            // if we've just deserialized some inlined objects,
            // e.g., strings, we should not wait for them, but
            // for the one before them
            while(objectsWrittenInline.Contains(idOfObjectToWaitFor))
            {
                idOfObjectToWaitFor--;
            }

            if(objectId >= idOfObjectToWaitFor)
            {
                if(!waitingList.HasElementsWaitingForLaterThan(objectId))
                {
                    Completed(objectId);
                    return;
                }

                // we need to ensure that this object is completed
                // after all previous one are completed
                idOfObjectToWaitFor = MaxAskedReferenceId + 1;
                while(objectsWrittenInline.Contains(idOfObjectToWaitFor))
                {
                    idOfObjectToWaitFor++;
                }
            }

            // it means we wait for some other objects, so we should store this information in waiting list
            waitingList.WaitFor(idOfObjectToWaitFor, objectId);
        }

        private void UpdateFields(Type actualType, object target)
        {
            var fieldOrTypeInfos = ((TypeFullDescriptor)actualType).FieldsToDeserialize;
            foreach(var fieldOrTypeInfo in fieldOrTypeInfos)
            {
                if(fieldOrTypeInfo.Field == null)
                {
                    ReadField(fieldOrTypeInfo.TypeToOmit);
                    continue;
                }
                var field = fieldOrTypeInfo.Field;
                if(field.IsDefined(typeof(TransientAttribute), false))
                {
                    if(field.IsDefined(typeof(ConstructorAttribute), false))
                    {
                        var ctorAttribute = (ConstructorAttribute)field.GetCustomAttributes(false).First(x => x is ConstructorAttribute);
                        field.SetValue(target, Activator.CreateInstance(field.FieldType, ctorAttribute.Parameters));
                    }
                    continue;
                }
                var value = ReadField(field.FieldType);
                field.SetValue(target, value);
            }
        }

        internal void Completed(int refId)
        {
            completedObjects.Enqueue(refId);
            while(completedObjects.Count > 0)
            {
                var currentReferenceId = completedObjects.Dequeue();
                var type = GetObjectByReferenceId(currentReferenceId).GetType();
                readMethods.completeMethodsProvider.GetOrCreate(type)(this, currentReferenceId);
            }
        }

        internal void CompletedInnerUsingReflection(int refId)
        {
            var obj = GetObjectByReferenceId(refId);
            var factoryId = objectsForSurrogates.FindMatchingIndex(obj.GetType());

            // (1) call post-deserialization hooks on it
            Helpers.InvokeAttribute(typeof(PostDeserializationAttribute), obj);
            var postHook = Helpers.GetDelegateWithAttribute(typeof(LatePostDeserializationAttribute), obj);
            if(postHook != null)
            {
                if(factoryId != -1)
                {
                    throw new InvalidOperationException(string.Format(LateHookAndSurrogateError, obj.GetType()));
                }
                postDeserializationHooks.Add(postHook);
            }
            if(postDeserializationCallback != null)
            {
                postDeserializationCallback(obj);
            }

            // (2) de-surrogate it if needed & clone the content
            if(factoryId != -1)
            {
                var desurrogated = objectsForSurrogates.GetByIndex(factoryId).DynamicInvoke(new[] { obj });
                // clone value of de-surrogated objects to final objects
                CloneContentUsingReflection(desurrogated, deserializedObjects[refId]);
                obj = deserializedObjects[refId];
                surrogatesWhileReading.Remove(refId);
            }

            // (3) inform waiting objects
            InformWaitingObjects(refId);
        }

        internal void InformWaitingObjects(int refId)
        {
            List<int> list;
            if(waitingList.TryGetElementsWaitingFor(refId, out list))
            {
                foreach(var element in list)
                {
                    completedObjects.Enqueue(element);
                }
                waitingList.RemoveWaitingListFor(refId);
            }
        }

        internal static void CloneContentUsingReflection(object source, object destination)
        {
            var sourceType = source.GetType();
            var destinationType = destination.GetType();

            if(sourceType != destinationType)
            {
                throw new ArgumentException("Source and destination types mismatched.");
            }

            foreach(var field in source.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                var srcValue = field.GetValue(source);
                field.SetValue(destination, srcValue == source ? destination : srcValue);
            }
        }

        private void ReadNotPrecreated(Type type, int objectId)
        {
            if(type.IsValueType)
            {
                // a boxed value type
                SetObjectByReferenceId(objectId, ReadField(type));
            }
            else if(typeof(MulticastDelegate).IsAssignableFrom(type))
            {
                ReadDelegate(type, objectId);
            }
            else if(type.IsArray)
            {
                ReadArray(type.GetElementType(), objectId);
            }
            else
            {
                throw new InvalidOperationException(InternalErrorMessage);
            }
        }

        private void UpdateElements(Type type, int objectId)
        {
            var obj = GetObjectByReferenceId(objectId);
            var speciallyDeserializable = obj as ISpeciallySerializable;
            if(speciallyDeserializable != null)
            {
                LoadAndVerifySpeciallySerializableAndVerify(speciallyDeserializable, reader);
                return;
            }
            CollectionMetaToken token;
            if(!CollectionMetaToken.TryGetCollectionMetaToken(type, out token))
            {
                throw new InvalidOperationException(InternalErrorMessage);
            }

            if(token.IsDictionary)
            {
                FillDictionary(token, objectId);
                return;
            }

            // so we can assume it is ICollection<T> or ICollection
            FillCollection(token.FormalElementType, objectId);
        }

        public int MaxAskedReferenceId
        {
            get; private set;
        }

        public void ResetMaxAskedReferenceId()
        {
            MaxAskedReferenceId = -1;
        }

        internal object GetObjectByReferenceId(int refId, bool forceDeserializedObject = false)
        {
            if(refId > MaxAskedReferenceId)
            {
                MaxAskedReferenceId = refId;
            }

            object obj;
            if(!forceDeserializedObject && surrogatesWhileReading.TryGetValue(refId, out obj))
            {
                return obj;
            }
            return deserializedObjects[refId];
        }

        internal void SetObjectByReferenceId(int refId, object obj)
        {
            if(surrogatesWhileReading.ContainsKey(refId))
            {
                surrogatesWhileReading[refId] = obj;
            }
            else
            {
                deserializedObjects[refId] = obj;
            }
        }

        internal int ReadAndTouchReference()
        {
            var refId = reader.ReadInt32();
            if(refId == Consts.NullObjectId)
            {
                // there was an explicit 'null' stored in stream
                return Consts.NullObjectId;
            }
            if(refId >= deserializedObjects.Count)
            {
                var type = ReadType();
                var isSurrogated = reader.ReadBoolean();
                if(isSurrogated)
                {
                    var objectTypeAfterDesurrogation = ReadType();
                    // using formatter service here is enough, as the whole content of an object will be cloned later
                    deserializedObjects[refId] = FormatterServices.GetUninitializedObject(objectTypeAfterDesurrogation.UnderlyingType);
                    var surrogate = readMethods.createObjectMethodsProvider.GetOrCreate(type.UnderlyingType)();
                    surrogatesWhileReading.Add(refId, surrogate);
                }

                readMethods.touchInlinedObjectMethodsProvider.GetOrCreate(type.UnderlyingType)(this, refId);
            }

            return refId;
        }

        internal void TryTouchInlinedObjectUsingReflection(Type type, int refId)
        {
            if(!TryTouchObject(type, refId))
            {
                if(!TryRecreateObjectUsingAdditionalMetadata(type, refId))
                {
                    ReadNotPrecreated(type, refId);
                    objectsWrittenInline.Add(refId);
                }
            }
        }

        private object ReadField(Type formalType)
        {
            if(Helpers.IsTransient(formalType))
            {
                return Helpers.GetDefaultValue(formalType);
            }

            if(!formalType.IsValueType)
            {
                var refId = ReadAndTouchReference();
                if(refId == Consts.NullObjectId)
                {
                    // there was an explicit 'null' stored in stream
                    return null;
                }
                return GetObjectByReferenceId(refId, true);
            }
            if(formalType.IsEnum)
            {
                var value = ReadField(Enum.GetUnderlyingType(formalType));
                return Enum.ToObject(formalType, value);
            }
            var nullableActualType = Nullable.GetUnderlyingType(formalType);
            if(nullableActualType != null)
            {
                var isNotNull = reader.ReadBoolean();
                return isNotNull ? ReadField(nullableActualType) : null;
            }
            if(Helpers.IsWriteableByPrimitiveWriter(formalType))
            {
                var methodName = string.Concat("Read", formalType.Name);
                var readMethod = typeof(PrimitiveReader).GetMethod(methodName);
                return readMethod.Invoke(reader, Type.EmptyTypes);
            }

            var returnedObject = Activator.CreateInstance(formalType);
            // here we have a boxed struct which we put to struct reference list
            UpdateFields(formalType, returnedObject);
            // if this is subtype
            return returnedObject;
        }

        private bool TryRecreateObjectUsingAdditionalMetadata(Type type, int objectId)
        {
            if(type.IsArray)
            {
                ReadMetadataAndTouchArray(type.GetElementType(), objectId);
                return true;
            }
            if(type == typeof(string))
            {
                var result = reader.ReadString();
                SetObjectByReferenceId(objectId, result);
                // this is needed to call post deserialization hooks...
                Completed(objectId);
                objectsWrittenInline.Add(objectId);
                return true;
            }
            return false;
        }

        private void FillCollection(Type elementFormalType, int objectId)
        {
            var obj = GetObjectByReferenceId(objectId);
            var collectionType = obj.GetType();
            var count = reader.ReadInt32();

            if(collectionType == typeof(Stack) ||
            (collectionType.IsGenericType && collectionType.GetGenericTypeDefinition() == typeof(Stack<>)))
            {
                var stack = (dynamic)obj;
                var temp = new dynamic[count];
                for(int i = 0; i < count; i++)
                {
                    temp[i] = ReadField(elementFormalType);
                }
                for(int i = count - 1; i >= 0; --i)
                {
                    stack.Push(temp[i]);
                }
            }
            else
            {
                var addMethod = collectionType.GetMethod("Add", new[] { elementFormalType }) ??
                                              collectionType.GetMethod("Enqueue", new[] { elementFormalType }) ??
                                              collectionType.GetMethod("Push", new[] { elementFormalType });
                if(addMethod == null)
                {
                    throw new InvalidOperationException(string.Format(CouldNotFindAddErrorMessage,
                                                                  collectionType));
                }
                Type delegateType;
                if(addMethod.ReturnType == typeof(void))
                {
                    delegateType = typeof(Action<>).MakeGenericType(new[] { elementFormalType });
                }
                else
                {
                    delegateType = typeof(Func<,>).MakeGenericType(new[] {
                        elementFormalType,
                        addMethod.ReturnType
                    });
                }

                var addDelegate = Delegate.CreateDelegate(delegateType, obj, addMethod);
                for(var i = 0; i < count; i++)
                {
                    var value = ReadField(elementFormalType);
                    addDelegate.DynamicInvoke(value);
                }
            }
        }

        private void FillDictionary(CollectionMetaToken token, int objectId)
        {
            var obj = GetObjectByReferenceId(objectId);
            var dictionaryType = obj.GetType();
            var count = reader.ReadInt32();
            var addMethodArgumentTypes = new[] {
                token.FormalKeyType,
                token.FormalValueType
            };
            var addMethod = dictionaryType.GetMethod("Add", addMethodArgumentTypes) ??
                   dictionaryType.GetMethod("TryAdd", addMethodArgumentTypes);
            if(addMethod == null)
            {
                throw new InvalidOperationException(string.Format(CouldNotFindAddErrorMessage,
                    dictionaryType));
            }
            Type delegateType;
            if(addMethod.ReturnType == typeof(void))
            {
                delegateType = typeof(Action<,>).MakeGenericType(addMethodArgumentTypes);
            }
            else
            {
                delegateType = typeof(Func<,,>).MakeGenericType(new[] {
                    addMethodArgumentTypes[0],
                    addMethodArgumentTypes[1],
                    addMethod.ReturnType
                });
            }
            var addDelegate = Delegate.CreateDelegate(delegateType, obj, addMethod);
            for(var i = 0; i < count; i++)
            {
                var key = ReadField(addMethodArgumentTypes[0]);
                var value = ReadField(addMethodArgumentTypes[1]);

                addDelegate.DynamicInvoke(new[] { key, value });
            }
        }

        private void ReadMetadataAndTouchArray(Type elementFormalType, int objectId)
        {
            var rank = reader.ReadInt32();
            var lengths = new int[rank];
            for(var i = 0; i < rank; i++)
            {
                lengths[i] = reader.ReadInt32();
            }
            var array = Array.CreateInstance(elementFormalType, lengths);
            // we should update the array object as soon as we can
            // why? because it can have the reference to itself (what a corner case!)
            SetObjectByReferenceId(objectId, array);
        }

        private void ReadArray(Type elementFormalType, int objectId)
        {
            var array = (Array)GetObjectByReferenceId(objectId);
            var position = new int[array.Rank];
            FillArrayRowRecursive(array, 0, position, elementFormalType);
        }

        private void ReadDelegate(Type type, int objectId)
        {
            var invocationListLength = reader.ReadInt32();
            if(invocationListLength == 0)
            {
                return;
            }
            Delegate result = null;
            for(var i = 0; i < invocationListLength; i++)
            {
                var target = ReadField(typeof(object));
                var method = Methods.Read();
                var del = Delegate.CreateDelegate(type, target, method.UnderlyingMethod);
                result = Delegate.Combine(result, del);
            }
            SetObjectByReferenceId(objectId, result);
        }

        private void FillArrayRowRecursive(Array array, int currentDimension, int[] position, Type elementFormalType)
        {
            var length = array.GetLength(currentDimension);
            for(var i = 0; i < length; i++)
            {
                if(currentDimension == array.Rank - 1)
                {
                    var value = ReadField(elementFormalType);
                    array.SetValue(value, position);
                }
                else
                {
                    FillArrayRowRecursive(array, currentDimension + 1, position, elementFormalType);
                }
                position[currentDimension]++;
                for(var j = currentDimension + 1; j < array.Rank; j++)
                {
                    position[j] = 0;
                }
            }
        }

        internal static void LoadAndVerifySpeciallySerializableAndVerify(ISpeciallySerializable obj, PrimitiveReader reader)
        {
            var beforePosition = reader.Position;
            obj.Load(reader);
            var afterPosition = reader.Position;
            var serializedLength = reader.ReadInt64();
            if(serializedLength + beforePosition != afterPosition)
            {
                throw new InvalidOperationException(string.Format(
                    "Stream corruption by '{0}', incorrent magic {1} when {2} expected.", obj.GetType(), serializedLength, afterPosition - beforePosition));
            }
        }

        internal TypeDescriptor ReadType()
        {
            return readTypeMethod();
        }

        private TypeSimpleDescriptor ReadSimpleTypeDescriptor()
        {
            TypeSimpleDescriptor type;
            var typeId = reader.ReadInt32();
            if(typeId == Consts.NullObjectId)
            {
                return null;
            }

            if(types.Count > typeId)
            {
                type = (TypeSimpleDescriptor)types[typeId];
            }
            else
            {
                type = new TypeSimpleDescriptor();
                types.Add(type);

                type.Read(this);
            }

            return type;
        }

        private TypeFullDescriptor ReadFullTypeDescriptor()
        {
            TypeFullDescriptor type;
            var typeIdOrPosition = reader.ReadInt32();
            if(typeIdOrPosition == Consts.NullObjectId)
            {
                return null;
            }

            var isGenericParameter = reader.ReadBoolean();
            if(isGenericParameter)
            {
                var genericType = ReadFullTypeDescriptor().UnderlyingType;
                type = (TypeFullDescriptor)genericType.GetGenericArguments()[typeIdOrPosition];
            }
            else
            {
                if(types.Count > typeIdOrPosition)
                {
                    type = (TypeFullDescriptor)types[typeIdOrPosition];
                }
                else
                {
                    type = new TypeFullDescriptor();
                    types.Add(type);

                    type.Read(this);
                }
            }

            if(type.UnderlyingType.IsGenericType)
            {
                var isOpen = reader.ReadBoolean();
                if(!isOpen)
                {
                    var args = new Type[type.UnderlyingType.GetGenericArguments().Count()];
                    for(int i = 0; i < args.Length; i++)
                    {
                        args[i] = ReadFullTypeDescriptor().UnderlyingType;
                    }

                    type = (TypeFullDescriptor)type.UnderlyingType.MakeGenericType(args);
                }
            }

            if(Helpers.ContainsGenericArguments(type.UnderlyingType))
            {
                var ranks = reader.ReadArray();
                if(ranks.Length > 0)
                {
                    var arrayDescriptor = new ArrayDescriptor(type.UnderlyingType, ranks);
                    type = (TypeFullDescriptor)arrayDescriptor.BuildArrayType();
                }
            }

            return type;
        }

        private bool TryTouchObject(Type actualType, int refId)
        {
            if(refId < deserializedObjects.Count)
            {
                return true;
            }

            var created = CreateObjectUsingReflection(actualType, treatCollectionAsUserObject);
            if(created != null)
            {
                SetObjectByReferenceId(refId, created);
            }
            return created != null;
        }

        internal static object CreateObjectUsingReflection(Type type, bool treatCollectionAsUserObject)
        {
            object result = null;
            switch(GetCreationWay(type, treatCollectionAsUserObject))
            {
                case CreationWay.Null:
                break;
                case CreationWay.DefaultCtor:
                result = Activator.CreateInstance(type);
                break;
                case CreationWay.Uninitialized:
                result = FormatterServices.GetUninitializedObject(type);
                break;
            }
            return result;
        }

        internal static CreationWay GetCreationWay(Type actualType, bool treatCollectionAsUserObject)
        {
            if(Helpers.CanBeCreatedWithDataOnly(actualType))
            {
                return CreationWay.Null;
            }
            if(!treatCollectionAsUserObject && CollectionMetaToken.IsCollection(actualType))
            {
                return CreationWay.DefaultCtor;
            }
            if(typeof(ISpeciallySerializable).IsAssignableFrom(actualType))
            {
                return CreationWay.DefaultCtor;
            }
            return CreationWay.Uninitialized;
        }

        internal bool TreatCollectionAsUserObject { get { return treatCollectionAsUserObject; } }
        internal PrimitiveReader PrimitiveReader { get { return reader; } }
        internal IdentifiedElementsList<ModuleDescriptor> Modules { get; private set; }
        internal IdentifiedElementsList<AssemblyDescriptor> Assemblies { get; private set; }
        internal IdentifiedElementsList<MethodDescriptor> Methods { get; private set; }
        internal VersionToleranceLevel VersionToleranceLevel { get; private set; }

        internal const string LateHookAndSurrogateError = "Type {0}: late post deserialization callback cannot be used in conjunction with surrogates.";

        internal readonly Action<object> postDeserializationCallback;
        internal readonly List<Action> postDeserializationHooks;
        internal readonly SwapList objectsForSurrogates;
        internal AutoResizingList<object> deserializedObjects;
        internal readonly OneToOneMap<int, object> surrogatesWhileReading;
        internal readonly Serializer.ReadMethods readMethods;

        private readonly HashSet<int> objectsWrittenInline;
        private readonly WaitCollection<int> waitingList;
        private readonly Queue<int> completedObjects;
        private readonly Func<TypeDescriptor> readTypeMethod;
        private List<TypeDescriptor> types;
        private WeakReference[] soFarDeserialized;
        private readonly bool treatCollectionAsUserObject;
        private ReferencePreservation referencePreservation;
        private PrimitiveReader reader;

        private const int InitialCapacity = 128;
        private const string InternalErrorMessage = "Internal error: should not reach here.";
        private const string CouldNotFindAddErrorMessage = "Could not find suitable Add method for the type {0}.";

        internal enum CreationWay
        {
            Uninitialized,
            DefaultCtor,
            Null
        }
    }

    internal delegate void ReadMethodDelegate(ObjectReader reader, Type type, int objectId);
    internal delegate void CompleteMethodDelegate(ObjectReader reader, int objectId);
    internal delegate void TouchInlinedObjectMethodDelegate(ObjectReader reader, int objectId);
    internal delegate object CreateObjectMethodDelegate();

    internal delegate void CloneMethodDelegate(object src, object dst);
}

