import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter_hbb/common.dart';
import 'package:flutter_hbb/desktop/pages/desktop_tab_page.dart';
import 'package:flutter_hbb/models/platform_model.dart';

class LanRolePage extends StatelessWidget {
  const LanRolePage({Key? key}) : super(key: key);

  @override
  Widget build(BuildContext context) {
    final role = bind.mainGetLanRole();
    if (role == 'controller') {
      return const DesktopTabPage();
    }
    if (role == 'host') {
      return const _HostPage();
    }
    return const DesktopTabPage();
  }
}

class _HostPage extends StatefulWidget {
  const _HostPage();

  @override
  State<_HostPage> createState() => _HostPageState();
}

class _HostPageState extends State<_HostPage> {
  Timer? _timer;
  int _status = 0;

  @override
  void initState() {
    super.initState();
    _update();
    _timer = Timer.periodic(const Duration(seconds: 1), (_) => _update());
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  void _update() {
    final status = bind.mainGetLanControllerStatus();
    if (mounted && status != _status) {
      setState(() => _status = status);
    }
  }

  @override
  Widget build(BuildContext context) {
    final text = _status == 0 ? '未连接' : (_status == 1 ? '已连接' : '已断开');
    final color = _status == 0
        ? const Color(0xFFF2B01E)
        : (_status == 1 ? const Color(0xFF20A464) : const Color(0xFFD64545));
    return Scaffold(
      backgroundColor: Colors.white,
      body: Center(
        child: Text(
          text,
          style: TextStyle(
            color: color,
            fontSize: 34,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
    );
  }
}
