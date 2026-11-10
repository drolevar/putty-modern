/*
 * engine.c - exported entry points of the PuTTY engine.
 */

#include <string.h>

#include "putty.h"          /* PuTTY core: Conf, CONF_* */
#include "putty_engine.h"

PE_API uint32_t pe_abi_version(void)
{
    return PE_ABI_VERSION;
}

PE_API int32_t pe_probe(void)
{
    static const char host[] = "example.invalid";

    Conf *conf = conf_new();
    conf_set_str(conf, CONF_host, host);
    const char *got = conf_get_str(conf, CONF_host);
    int32_t ok = (got != NULL && strcmp(got, host) == 0);
    conf_free(conf);
    return ok;
}
