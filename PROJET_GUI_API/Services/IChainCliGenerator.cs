using System.Collections.Generic;
using ApiTester.Models;

namespace ApiTester.Services;

public interface IChainCliGenerator
{
    string Generate(IReadOnlyList<ChainStep> steps, ChainCliLanguage language);
}
