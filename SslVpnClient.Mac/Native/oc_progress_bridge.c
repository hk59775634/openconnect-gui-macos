#include <stdarg.h>
#include <stdio.h>

typedef void (*oc_managed_progress_fn)(void *privdata, int level, const char *msg);

static oc_managed_progress_fn g_handler;

void oc_set_progress_handler(oc_managed_progress_fn fn)
{
    g_handler = fn;
}

static void oc_progress_variadic(void *privdata, int level, const char *fmt, ...)
{
    char buf[4096];
    va_list ap;

    if (!g_handler)
        return;

    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    g_handler(privdata, level, buf);
}

void *oc_get_progress_callback(void)
{
    return (void *)oc_progress_variadic;
}
