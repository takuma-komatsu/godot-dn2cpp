using System;
using System.Collections.Generic;

namespace GodotTools.Export
{
    internal sealed class Dn2CppOptimizationOptions
    {
        private readonly bool _trimReflection;
        private readonly bool _trimGodotClasses;
        private readonly bool _sharedGenerics;

        public Dn2CppOptimizationOptions(Func<string, bool?> getOption)
        {
            // Existing presets lack these keys and must inherit the export defaults.
            _trimReflection = getOption("trim_reflection") ?? true;
            _trimGodotClasses = getOption("trim_godot_classes") ?? true;
            _sharedGenerics = getOption("shared_generics") ?? true;
        }

        public void AppendArguments(List<string> arguments)
        {
            if (_trimReflection)
                arguments.Add("--trim-reflection");
            if (_trimGodotClasses)
                arguments.Add("--trim-godot-classes");
            if (!_sharedGenerics)
                arguments.Add("--no-shared-generics");
        }
    }
}
