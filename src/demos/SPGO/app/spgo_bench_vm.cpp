// spgo_bench_vm.cpp - Tiny stack VM for demonstrating SPGO improvements.
//
// Without SPGO:
//   cl /O2 /EHsc /GL /Zi spgo_bench_vm.cpp /link /debug
//
// With SPGO:
//   cl /O2 /EHsc /GL /Zi spgo_bench_vm.cpp /link /debug /spgo /spdin:spgo_bench_vm.spd
//
// Expected: 20-40% faster with SPGO due to dispatch loop optimization.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

static double GetTimeMs()
{
    LARGE_INTEGER freq, counter;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&counter);
    return (double)counter.QuadPart * 1000.0 / (double)freq.QuadPart;
}

enum Opcode : unsigned char
{
    OP_PUSH,   // push immediate
    OP_POP,    // discard TOS
    OP_ADD,    // pop two, push sum
    OP_SUB,    // pop b, pop a, push a-b
    OP_INC,    // increment TOS in place
    OP_LOAD,   // push locals[operand]
    OP_STORE,  // pop into locals[operand]
    OP_JMP,    // jump to operand
    OP_JZ,     // pop; jump if zero
    OP_PRINT,  // print TOS (no pop)
    OP_HALT,   // stop
};

struct Instr
{
    Opcode op;
    int    operand;
};

#define STACK_SZ  256
#define LOCAL_SZ  16

__declspec(noinline)
static long long RunVM(const Instr* code, int codeLen)
{
    int stack[STACK_SZ];
    int locals[LOCAL_SZ];
    int sp = 0;
    int ip = 0;
    long long cycles = 0;

    memset(locals, 0, sizeof(locals));

    while (ip < codeLen)
    {
        Opcode op  = code[ip].op;
        int    arg = code[ip].operand;
        cycles++;

        switch (op)
        {
        case OP_PUSH:  stack[sp++] = arg;                        ip++; break;
        case OP_POP:   sp--;                                     ip++; break;
        case OP_ADD:   { int b = stack[--sp]; stack[sp-1] += b;  ip++; break; }
        case OP_SUB:   { int b = stack[--sp]; stack[sp-1] -= b;  ip++; break; }
        case OP_INC:   stack[sp-1]++;                            ip++; break;
        case OP_LOAD:  stack[sp++] = locals[arg];                ip++; break;
        case OP_STORE: locals[arg] = stack[--sp];                ip++; break;
        case OP_JMP:   ip = arg;                                       break;
        case OP_JZ:    ip = (stack[--sp] == 0) ? arg : ip + 1;        break;
        case OP_PRINT: printf("%d\n", stack[sp-1]);              ip++; break;
        case OP_HALT:  return cycles;
        }
    }
    return cycles;
}

// Program: iterative fibonacci(N), repeated M times.
// Exercises the dispatch loop with heavily biased branches (inner loop
// body runs N times per iteration, outer branch taken once per M).
//
// locals: 0=rep, 1=a, 2=b, 3=i, 4=N, 5=M, 6=tmp

enum { L_REP, L_A, L_B, L_I, L_N, L_M, L_TMP };

int main(int argc, char** argv)
{
    int N = 30;         // fibonacci index
    int M = 5000000;    // repetitions

    if (argc > 1) N = atoi(argv[1]);
    if (argc > 2) M = atoi(argv[2]);

    Instr prog[] =
    {
        // Setup
        /* 0*/ {OP_PUSH,  N},
        /* 1*/ {OP_STORE, L_N},
        /* 2*/ {OP_PUSH,  M},
        /* 3*/ {OP_STORE, L_M},
        /* 4*/ {OP_PUSH,  0},         // rep = 0
        /* 5*/ {OP_STORE, L_REP},

        // Outer loop: if (rep - M == 0) goto done
        /* 6*/ {OP_LOAD,  L_REP},
        /* 7*/ {OP_LOAD,  L_M},
        /* 8*/ {OP_SUB,   0},
        /* 9*/ {OP_JZ,    36},        // -> done

        // Init fib: a=0, b=1, i=0
        /*10*/ {OP_PUSH,  0},
        /*11*/ {OP_STORE, L_A},
        /*12*/ {OP_PUSH,  1},
        /*13*/ {OP_STORE, L_B},
        /*14*/ {OP_PUSH,  0},
        /*15*/ {OP_STORE, L_I},

        // Inner loop: if (i - N == 0) goto end_inner
        /*16*/ {OP_LOAD,  L_I},
        /*17*/ {OP_LOAD,  L_N},
        /*18*/ {OP_SUB,   0},
        /*19*/ {OP_JZ,    32},        // -> end_inner

        // Body: tmp=a+b; a=b; b=tmp
        /*20*/ {OP_LOAD,  L_A},
        /*21*/ {OP_LOAD,  L_B},
        /*22*/ {OP_ADD,   0},
        /*23*/ {OP_STORE, L_TMP},
        /*24*/ {OP_LOAD,  L_B},
        /*25*/ {OP_STORE, L_A},
        /*26*/ {OP_LOAD,  L_TMP},
        /*27*/ {OP_STORE, L_B},

        // i++; goto inner loop
        /*28*/ {OP_LOAD,  L_I},
        /*29*/ {OP_INC,   0},
        /*30*/ {OP_STORE, L_I},
        /*31*/ {OP_JMP,   16},        // -> inner loop

        // end_inner: rep++; goto outer loop
        /*32*/ {OP_LOAD,  L_REP},
        /*33*/ {OP_INC,   0},
        /*34*/ {OP_STORE, L_REP},
        /*35*/ {OP_JMP,   6},         // -> outer loop

        // done: print result
        /*36*/ {OP_LOAD,  L_A},
        /*37*/ {OP_PRINT, 0},
        /*38*/ {OP_HALT,  0},
    };

    const int progLen = sizeof(prog) / sizeof(prog[0]);

    // Warmup run (1 iteration)
    Instr warmup[progLen];
    memcpy(warmup, prog, sizeof(prog));
    warmup[2].operand = 1;
    RunVM(warmup, progLen);

    // Timed run
    double start = GetTimeMs();
    long long cycles = RunVM(prog, progLen);
    double elapsed = GetTimeMs() - start;

    fprintf(stderr, "Executed %lld VM instructions in %.1f ms (%.1f MIPS)\n",
            cycles, elapsed, (double)cycles / elapsed / 1000.0);

    return 0;
}
