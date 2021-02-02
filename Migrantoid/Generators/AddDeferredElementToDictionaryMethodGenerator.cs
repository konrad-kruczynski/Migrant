// *******************************************************************
//
// Copyright (c) 2021 Konrad Kruczyński
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// *******************************************************************
using System;
using System.Reflection.Emit;

namespace Migrantoid.Generators
{
    internal sealed class AddDeferredElementToHashCodeBasedMethodGenerator : DynamicReadMethodGenerator<AddDeferredHashCodeBasedElementDelegate>
    {
        public AddDeferredElementToHashCodeBasedMethodGenerator(Type type)
            : base(type, "AddDeferredElement")
        {
        }

        protected override void InnerGenerate(ReaderGenerationContext context)
        {
            context.Generator.Emit(OpCodes.Ldarg_0);
            context.Generator.Emit(OpCodes.Ldarg_1);
            var addMethod = type.GetMethod("Add");
            if (addMethod.GetParameters().Length == 2)
            {
                context.Generator.Emit(OpCodes.Ldarg_2);
            }
            context.Generator.Emit(OpCodes.Callvirt, addMethod);

            if (addMethod.ReturnType != typeof(void))
            {
                context.Generator.Emit(OpCodes.Pop);
            }
        }
    }
}
