#!/usr/bin/env python
from math import sqrt, degrees, radians, sin, cos, exp
from random import random, randint, seed

#seed(123456)

def line_point_distance_xz(p, q, t):
    """Return closest distance between 3D point t
    and the line between points p and q *in the XZ plane*"""
    
    x1 = p[0]
    y1 = p[1]
    z1 = p[2]
    
    x2 = q[0]
    y2 = q[1]
    z2 = q[2]
    
    x0 = t[0]
    y0 = t[1]
    z0 = t[2]
    
    num = abs((x2-x1)*(z1-z0) - (x1-x0)*(z2-z1))    
    den = sqrt((x2-x1)**2 + (z2-z1)**2)
    return num/den
    
def behind_xz(p, q, t):
    """Return if point t is behind the observer at p (who is viewing from p to q).
    Ignores Y"""
    v = (q[0]-p[0], q[1]-p[1], q[2]-p[2])
    w = (t[0]-p[0], t[1]-p[1], t[2]-p[2])
    return v[0]*w[0] + v[2]*w[2] < 0
    
    
def rot_y(p, angle_degrees):
    """Positive rotation = CCW"""
    a = radians(angle_degrees)
    cos_a = cos(a)
    sin_a = sin(a)
    
    qx = cos_a*p[0] + sin_a*p[2]
    qz = -sin_a*p[0] + cos_a*p[2]

    return (qx, p[1], qz)
    
if False:
    print(line_point_distance_xz((0,0,0), (1,0,0), (0.5,0,0)))
    print(line_point_distance_xz((0,0,0), (1,0,0), (0.5,0,1)))
    
    print(line_point_distance_xz((0,0,0), (1,0,1), (0.5,0,0.5)))
    print(line_point_distance_xz((0,0,0), (1,0,1), (0.5,0,2)))

if False:
    print(rot_y((1,0,0), 90))
    print(rot_y((1,0,0), 45))
    print(rot_y((1,0,0), 0))
    
    print(rot_y((0.88,0,0.88), -45))
    
    
def compute_energy(tx, ty, tz, r):
    """Returns RMS in meters"""
    
    e2 = 0.0
    n = 0    
        
    for ref_name, ref_position in GROUND_TRUTH.items():
        #print(ref_name)
        
        r2 = rot_y(ref_position, r)
        r2 = (r2[0] + tx, r2[1] + ty, r2[2] + tz)
        
        for observation in OBSERVATIONS:            
            if observation['lm'] != ref_name:
                # XXX yuck
                continue            
            ob_origin = observation['head_pos']
            ob_direction = observation['head_ori']
            ob2 = (ob_origin[0] + ob_direction[0], ob_origin[1] + ob_direction[1], ob_origin[2] + ob_direction[2])
            dist = line_point_distance_xz(ob_origin, ob2, r2)
            
            # Penalize solution when point is behind, as seen along viewing direction
            if behind_xz(ob_origin, ob2, r2):
                dist += 1000
                
            #max_dist = max(max_dist, dist)
            #print(observation['lm'], dist)
            e2 += dist*dist
            
        n += 1
            
        if n == 0:
            print('WARNING: no observations for "%s"' % ref_name)
                    
        #print(max_dist)
        
    #return max_dist
    return sqrt(e2/n)
    
def rand_unit():
    return (random()-0.5)*2
    
if __name__ == '__main__':

    # Ground truth (in coord system to align to)
    # Point coordinates
    # SK world coordinate system, i.e. +Y up
    GROUND_TRUTH = {
        'RT': (-891.0309, 148, 701.9933), 
        'HKF': (222.3811, 33.5, -189.894100),
        'HKB': (259.083, 30.5, -178.2989),
        'BRN': (303.6142, 30.5, 79.835310),
        'DHL': (-476.4835, 64, 254.3338),
        'SS': (-52.751860, 11, -22.81),
        #'Tree': (50.029740, 9, 115.9509)
    }    

    # Observations as position + direction vector.
    # Note: head pos Y (height) is relative to starting head position
    # and not above floor
    OBSERVATIONS = [
        {"lm":"HKB","head_pos":[-0.144786, 0.057414, 0.179659],"head_ori":[0.166631, 0.251750, 0.953340]},
        {"lm":"HKF","head_pos":[-0.106714, 0.054435, 0.165516],"head_ori":[0.567109, 0.214798, 0.795142]},
        {"lm":"RT","head_pos":[1.752961, 0.516924, -8.370631],"head_ori":[0.206662, 0.068152, -0.976036]},
        {"lm":"DHL","head_pos":[1.885210, 0.535351, -8.261091],"head_ori":[0.358756, 0.031792, -0.932890]},
        {"lm":"SS","head_pos":[1.580237, 0.554369, -9.275459],"head_ori":[0.530321, -0.018952, -0.847585]},
        {"lm":"BRN","head_pos":[2.233647, 0.309135, -9.938712],"head_ori":[-0.945653, 0.053625, -0.320727]},
        {"lm":"SS","head_pos":[-0.193687, 0.547925, -6.495636],"head_ori":[0.534311, -0.042488, -0.844219]}
    ]
    
    # Compute the transformation rot(R) followed by T(tx,ty,tz)
    # that maps the sky coordinate system (in which the ground truth
    # points are defined) onto the observation coordinate system
    
    tx = 0.0
    ty = 0.0
    tz = 0.0
    r = 0.0
        
    best_tx = tx
    best_ty = ty
    best_tz = tz
    best_r = r
    best_energy = compute_energy(tx, ty, tz, r)
    print('[initial] Energy %.6f | tx=%.6f, ty=%.6f, tz=%.6f, r=%.6f' % \
        (best_energy, best_tx, best_ty, best_tz, best_r))
    
    K = 50000
    WO = 2000
    without_improvement = 0
    
    for k in range(0, K):
        
        T = 1 - (k+1)/K
        #print('Iter %d, T = %.6f' % (k, T))
        
        # Pick neighbour
        can_tx = tx
        can_ty = ty
        can_tz = tz
        can_r = r
        
        idx = randint(0, 2)
        if idx == 0:
            #print('Mutating tx')            
            can_tx = tx + rand_unit()*2000*T
        # NOTE: ty never mutated
        elif idx == 1:
            #print('Mutating tz')
            can_tz = tz + rand_unit()*2000*T
        elif idx == 2:
            #print('Mutating r')
            can_r = (r + rand_unit()*360*T) % 360
        #elif idx == 3:
        #    # Negate all values
        #    can_tx = -tx
        #    can_tz = -tz
        #    can_r = -r
            
        energy = compute_energy(can_tx, can_ty, can_tz, can_r)
        
        #print('[%05d] Energy %8.3f (candidate, idx %d) | can_tx=%.6f, can_ty=%.6f, can_tz=%6f, can_r=%.6f' % \
        #    (k, energy, idx, can_tx, can_ty, can_tz, can_r))
            
        if energy < best_energy:
            print('[%05d] Energy %8.3f (new best)         | tx=%.6f, ty=%.6f, tz=%6f, r=%.6f' % \
                (k, energy, can_tx, can_ty, can_tz, can_r))
            best_energy = energy
            best_tx = can_tx
            best_ty = can_ty
            best_tz = can_tz
            best_r = can_r
            without_improvement = 0
            tx = can_tx
            ty = can_ty
            tz = can_tz
            r = can_r
        elif T > 0:
            without_improvement += 1
            if without_improvement == WO:
                # Restart with last best solution
                print('[%05d] %d steps without improvement, restarting' % (k, WO))
                tx = best_tx
                ty = best_ty
                tz = best_tz
                r = best_r
                without_improvement = 0
                continue
                
            #print(energy, best_energy, T)
            p = exp(-(energy-best_energy)/T)
            if random() <= p:
                #print('Transitioning to less optimal solution (p=%.6f)' % p)
                tx = can_tx
                ty = can_ty
                tz = can_tz
                r = can_r
                
    print('Best: tx %.6f, ty %.6f, tz %.6f, r %.6f (energy %.6f)' % \
        (best_tx, best_ty, best_tz, best_r, best_energy))
        
    
