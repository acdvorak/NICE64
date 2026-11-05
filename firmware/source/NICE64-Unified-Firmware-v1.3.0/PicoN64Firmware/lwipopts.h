#ifndef NICE64_LWIPOPTS_H
#define NICE64_LWIPOPTS_H

/* Bare-metal RAW API, polled exclusively by Core1. */
#define NO_SYS                     1
#define SYS_LIGHTWEIGHT_PROT       0
#define LWIP_RAW                   1
#define LWIP_NETCONN               0
#define LWIP_SOCKET                0

/* Step 2C.2: DHCP plus one bounded RAW-API TCP control service. */
#define LWIP_DHCP                  1
#define LWIP_DNS                   1
#define LWIP_TCP                   1
#define LWIP_UDP                   1
#define LWIP_IPV4                  1
#define LWIP_IPV6                  0
#define LWIP_ICMP                  0
#define LWIP_IGMP                  0

#define MEM_ALIGNMENT              4
#define MEM_SIZE                   16384
#define MEMP_NUM_TCP_PCB           3
#define MEMP_NUM_TCP_PCB_LISTEN    1
#define MEMP_NUM_TCP_SEG           8
#define TCP_MSS                    1460
#define TCP_WND                    2920
#define TCP_SND_BUF                2920
#define TCP_SND_QUEUELEN           8
#define PBUF_POOL_SIZE             8
#define LWIP_STATS                 0
#define LWIP_STATS_DISPLAY         0

#endif
