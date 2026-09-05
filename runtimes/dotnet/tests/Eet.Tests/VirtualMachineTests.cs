using System.Text;
using Eet.Core;
using Xunit;

namespace Eet.Tests;

/// <summary>
/// The interpreter itself: termination statuses, the trap protocol, and the frame model.
/// </summary>
/// <remarks>
/// Byte offsets are written out beside the instructions that need them, because a trap
/// line names the address of the trapping instruction and those assertions are only as
/// good as the arithmetic behind them.
/// </remarks>
public class VirtualMachineTests
{
    [Fact]
    public void HaltExitsWithStatusZeroAndPrintsNothing()
    {
        RunResult result = new TestProgram().Emit(Opcode.Halt).Run();

        Assert.Equal(Isa.ExitOk, result.Status);
        Assert.Empty(result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(300, 44)]
    [InlineData(-1, 255)]
    [InlineData(256, 0)]
    public void ReturningFromTheEntryFrameExitsWithTheLowByte(int returned, int expected)
    {
        RunResult result = new TestProgram().Push(returned).Emit(Opcode.Ret).Run();

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void PrintRendersTheSmallestValueWithoutOverflowing()
    {
        RunResult result = new TestProgram()
            .Push(int.MinValue)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("-2147483648", result.Text);
        Assert.Equal(Isa.ExitOk, result.Status);
    }

    [Fact]
    public void ArithmeticWrapsRatherThanTrappingMidProgram()
    {
        RunResult result = new TestProgram()
            .Push(int.MaxValue)
            .Push(1)
            .Emit(Opcode.Add)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("-2147483648", result.Text);
    }

    [Fact]
    public void TheSmallestValueDividedByMinusOneRunsToCompletion()
    {
        // The one arithmetic case where the obvious C# translation throws rather than
        // producing a value; div must give int.MinValue and mod must give 0 (7.2).
        RunResult result = new TestProgram()
            .Push(int.MinValue)
            .Push(-1)
            .Emit(Opcode.Div)
            .Emit(Opcode.Print)
            .Push(int.MinValue)
            .Push(-1)
            .Emit(Opcode.Mod)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("-21474836480", result.Text);
        Assert.Equal(Isa.ExitOk, result.Status);
    }

    [Fact]
    public void PrintcWritesOneByteMaskedToEightBits()
    {
        RunResult result = new TestProgram()
            .Push(0x141) // 'A', plus a bit above the byte the spec keeps
            .Emit(Opcode.PrintC)
            .Push(10)
            .Emit(Opcode.PrintC)
            .Emit(Opcode.Halt)
            .Run();

        // A bare LF, never CRLF: output is a byte stream with no translation layer (4.6).
        Assert.Equal(new byte[] { 0x41, 0x0A }, result.Stdout);
    }

    [Fact]
    public void PrintsCopiesDataVerbatim()
    {
        RunResult result = new TestProgram()
            .WithData(Encoding.ASCII.GetBytes("hello"))
            .Push(1) // addr
            .Push(3) // len, on top
            .Emit(Opcode.PrintS)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("ell", result.Text);
    }

    [Fact]
    public void ComparisonsAreSigned()
    {
        // An unsigned comparison would make -1 the larger value and print 0.
        RunResult result = new TestProgram()
            .Push(-1)
            .Push(1)
            .Emit(Opcode.Lt)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("1", result.Text);
    }

    [Fact]
    public void RotRotatesTheTopThreeLeft()
    {
        // a b c -> b c a, so printing pops a, then c, then b.
        RunResult result = new TestProgram()
            .Push(1)
            .Push(2)
            .Push(3)
            .Emit(Opcode.Rot)
            .Emit(Opcode.Print)
            .Emit(Opcode.Print)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("132", result.Text);
    }

    [Fact]
    public void OverCopiesTheSecondItemToTheTop()
    {
        // a b -> a b a, so printing pops a, then b, then a.
        RunResult result = new TestProgram()
            .Push(1)
            .Push(2)
            .Emit(Opcode.Over)
            .Emit(Opcode.Print)
            .Emit(Opcode.Print)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("121", result.Text);
    }

    [Fact]
    public void OperandStacksArePrivateToTheirFrame()
    {
        //  0 push 111 | 5 push 222 | 10 call 18,0,0 | 17 halt | 18 pop
        RunResult result = new TestProgram()
            .Push(111)
            .Push(222)
            .Call(18, 0, 0)
            .Emit(Opcode.Halt)
            .Emit(Opcode.Pop)
            .Run();

        // The callee's own stack is empty, so the caller's two values are unreachable.
        Assert.Equal(Isa.ExitTrap, result.Status);
        Assert.Equal("eet: trap T01: stack underflow at pc=00000012\n", result.Stderr);
    }

    [Fact]
    public void CallerKeepsItsStackAcrossACall()
    {
        //  0 push 5 | 5 call 15,0,0 | 12 add | 13 print | 14 halt | 15 push 7 | 20 ret
        RunResult result = new TestProgram()
            .Push(5)
            .Call(15, 0, 0)
            .Emit(Opcode.Add)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Push(7)
            .Emit(Opcode.Ret)
            .Run();

        Assert.Equal("12", result.Text);
        Assert.Equal(Isa.ExitOk, result.Status);
    }

    [Fact]
    public void ArgumentsLandInLocalsInSourceOrder()
    {
        // The first value pushed becomes locals[0] (5.4 step 3).
        //  0 push 11 | 5 push 22 | 10 call 19,2,2 | 17 pop | 18 halt
        // 19 load 0 | 21 print | 22 load 1 | 24 print | 25 push 0 | 30 ret
        RunResult result = new TestProgram()
            .Push(11)
            .Push(22)
            .Call(19, 2, 2)
            .Emit(Opcode.Pop)
            .Emit(Opcode.Halt)
            .Emit(Opcode.Load, 0)
            .Emit(Opcode.Print)
            .Emit(Opcode.Load, 1)
            .Emit(Opcode.Print)
            .Push(0)
            .Emit(Opcode.Ret)
            .Run();

        Assert.Equal("1122", result.Text);
    }

    [Fact]
    public void EachCallGetsFreshlyZeroedLocals()
    {
        // Both calls run at the same depth. This runtime gives every depth a fixed locals
        // window, so the second call would see the first call's 42 if the window were not
        // scrubbed on entry; 5.4 step 4 requires zeros.
        //  0 call 17,0,1 | 7 pop | 8 call 17,0,1 | 15 pop | 16 halt
        // 17 load 0 | 19 print | 20 push 42 | 25 store 0 | 27 push 0 | 32 ret
        RunResult result = new TestProgram()
            .Call(17, 0, 1)
            .Emit(Opcode.Pop)
            .Call(17, 0, 1)
            .Emit(Opcode.Pop)
            .Emit(Opcode.Halt)
            .Emit(Opcode.Load, 0)
            .Emit(Opcode.Print)
            .Push(42)
            .Emit(Opcode.Store, 0)
            .Push(0)
            .Emit(Opcode.Ret)
            .Run();

        Assert.Equal("00", result.Text);
    }

    [Fact]
    public void DeepButLegalRecursionReturnsThroughEveryFrame()
    {
        //  f(n) = n == 0 ? 0 : f(n - 1) + 1, called with 200: 202 live frames at the
        //  deepest point, comfortably inside the limit, and every one of them has to hand
        //  its result back to the frame beneath.
        //   0 push 200 | 5 call 14,1,1 | 12 print | 13 halt
        //  14 load 0 | 16 jz 43 | 21 load 0 | 23 push 1 | 28 sub | 29 call 14,1,1
        //  36 push 1 | 41 add | 42 ret | 43 push 0 | 48 ret
        RunResult result = new TestProgram()
            .Push(200)
            .Call(14, 1, 1)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Emit(Opcode.Load, 0)
            .EmitU32(Opcode.Jz, 43)
            .Emit(Opcode.Load, 0)
            .Push(1)
            .Emit(Opcode.Sub)
            .Call(14, 1, 1)
            .Push(1)
            .Emit(Opcode.Add)
            .Emit(Opcode.Ret)
            .Push(0)
            .Emit(Opcode.Ret)
            .Run();

        Assert.Equal("200", result.Text);
        Assert.Equal(Isa.ExitOk, result.Status);
    }

    [Fact]
    public void UnboundedRecursionIsACallDepthTrapRatherThanAHostStackOverflow()
    {
        // 0 call 7,0,0 | 7 call 7,0,0 | 14 ret
        RunResult result = new TestProgram()
            .Call(7, 0, 0)
            .Call(7, 0, 0)
            .Emit(Opcode.Ret)
            .Run();

        Assert.Equal(Isa.ExitTrap, result.Status);
        Assert.Equal("eet: trap T03: call depth exceeded at pc=00000007\n", result.Stderr);
    }

    [Fact]
    public void OutputWrittenBeforeATrapStillReachesStdout()
    {
        RunResult result = new TestProgram()
            .WithData(Encoding.ASCII.GetBytes("before\n"))
            .Push(0)
            .Push(7)
            .Emit(Opcode.PrintS)
            .Push(1)
            .Push(0)
            .Emit(Opcode.Div)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("before\n", result.Text);
        Assert.Equal(Isa.ExitTrap, result.Status);
        Assert.StartsWith(
            "eet: trap T04: division by zero at pc=",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrapLineIsExactlyTheFormatSectionSixGives()
    {
        // The pc is the first byte of the trapping instruction - the mod at offset 10 -
        // in eight uppercase hex digits, and the line ends in a single LF.
        RunResult result = new TestProgram()
            .Push(1)
            .Push(0)
            .Emit(Opcode.Mod)
            .Run();

        Assert.Equal("eet: trap T04: division by zero at pc=0000000A\n", result.Stderr);
        Assert.Equal(Isa.ExitTrap, result.Status);
    }

    [Fact]
    public void TheUserTrapCarriesItsCodeInDecimal()
    {
        RunResult result = new TestProgram().Emit(Opcode.Trap, 7).Run();

        Assert.Equal("eet: trap T10: trap instruction (code=7) at pc=00000000\n", result.Stderr);
        Assert.Equal(Isa.ExitTrap, result.Status);
    }

    [Fact]
    public void AnUnknownOpcodeIsInvalidOpcode()
    {
        RunResult result = new TestProgram().Raw(0xEE).Run();

        Assert.Equal("eet: trap T05: invalid opcode at pc=00000000\n", result.Stderr);
    }

    [Fact]
    public void ImmediatesRunningPastTheCodeEndAreInvalidOpcode()
    {
        // A push whose four-byte operand is cut short by the end of the section.
        RunResult result = new TestProgram().Raw((byte)Opcode.Push, 0x01, 0x02).Run();

        Assert.Equal("eet: trap T05: invalid opcode at pc=00000000\n", result.Stderr);
    }

    [Fact]
    public void FallingOffTheEndOfTheCodeIsJumpOutOfRange()
    {
        // The only trap that reports the program counter rather than an instruction.
        RunResult result = new TestProgram().Emit(Opcode.Nop).Run();

        Assert.Equal("eet: trap T09: jump out of range at pc=00000001\n", result.Stderr);
    }

    [Fact]
    public void ABranchPastTheCodeEndIsJumpOutOfRange()
    {
        RunResult result = new TestProgram().EmitU32(Opcode.Jmp, 9999).Run();

        Assert.Equal("eet: trap T09: jump out of range at pc=00000000\n", result.Stderr);
    }

    [Fact]
    public void AnUntakenBranchDoesNotValidateItsTarget()
    {
        // 4.4: the target is checked when the branch is taken, not when it is decoded.
        RunResult result = new TestProgram()
            .Push(1)
            .EmitU32(Opcode.Jz, 9999)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal(Isa.ExitOk, result.Status);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void DloadOutsideTheDataSectionIsADataAccessTrap(int address)
    {
        RunResult result = new TestProgram()
            .WithData(1, 2, 3)
            .Push(address)
            .Emit(Opcode.DLoad)
            .Run();

        Assert.Equal("eet: trap T08: data access out of range at pc=00000005\n", result.Stderr);
    }

    [Fact]
    public void DloadZeroExtends()
    {
        RunResult result = new TestProgram()
            .WithData(0xFF)
            .Push(0)
            .Emit(Opcode.DLoad)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("255", result.Text);
    }

    [Fact]
    public void PrintsRejectsARangeThatWouldOverflowThirtyTwoBitArithmetic()
    {
        // addr + len wraps to -2 in int arithmetic, which looks in range to a runtime that
        // adds without widening first.
        RunResult result = new TestProgram()
            .WithData(1, 2, 3)
            .Push(int.MaxValue)
            .Push(int.MaxValue)
            .Emit(Opcode.PrintS)
            .Run();

        Assert.Equal(Isa.ExitTrap, result.Status);
        Assert.StartsWith("eet: trap T08:", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void ALocalIndexBeyondTheFrameIsALocalTrap()
    {
        RunResult result = new TestProgram().WithEntryLocals(2).Emit(Opcode.Load, 2).Run();

        Assert.Equal("eet: trap T06: local index out of range at pc=00000000\n", result.Stderr);
    }

    [Fact]
    public void MoreArgumentsThanLocalsIsALocalTrap()
    {
        RunResult result = new TestProgram().Call(0, 2, 1).Run();

        Assert.Equal("eet: trap T06: local index out of range at pc=00000000\n", result.Stderr);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void AComputedGlobalIndexOutsideTheArrayIsAGlobalTrap(int index)
    {
        RunResult result = new TestProgram()
            .WithGlobals(4)
            .Push(index)
            .Emit(Opcode.GLoadX)
            .Run();

        Assert.Equal("eet: trap T07: global index out of range at pc=00000005\n", result.Stderr);
    }

    [Fact]
    public void GstorexTakesTheIndexFromTheTopOfTheStack()
    {
        RunResult result = new TestProgram()
            .WithGlobals(4)
            .Push(99) // value
            .Push(2)  // index, on top
            .Emit(Opcode.GStoreX)
            .EmitU16(Opcode.GLoad, 2)
            .Emit(Opcode.Print)
            .Emit(Opcode.Halt)
            .Run();

        Assert.Equal("99", result.Text);
    }

    [Fact]
    public void APushOntoAFullOperandStackIsAStackOverflow()
    {
        TestProgram program = new();
        for (int i = 0; i <= Isa.MaxOperandStack; i++)
        {
            program.Push(i);
        }

        RunResult result = program.Emit(Opcode.Halt).Run();

        Assert.Equal(Isa.ExitTrap, result.Status);
        Assert.StartsWith(
            "eet: trap T02: stack overflow at pc=",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FillingTheOperandStackToTheLimitIsFine()
    {
        TestProgram program = new();
        for (int i = 0; i < Isa.MaxOperandStack; i++)
        {
            program.Push(i);
        }

        RunResult result = program.Emit(Opcode.Print).Emit(Opcode.Halt).Run();

        Assert.Equal(Isa.ExitOk, result.Status);
        Assert.Equal("1023", result.Text);
    }
}
